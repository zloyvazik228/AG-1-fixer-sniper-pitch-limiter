using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using Microsoft.Extensions.Logging;

namespace SniperPitchLimiter;

public sealed class PitchConfig : BasePluginConfig
{
    public bool Enabled { get; set; } = false;
    public float MaxDownPitch { get; set; } = 15;
    public bool SnipersOnly { get; set; } = true;
    public int GraphDownSign { get; set; } = 1;
}

[MinimumApiVersion(379)]
public sealed class SniperPitchLimiter : BasePlugin, IPluginConfig<PitchConfig>
{
    public override string ModuleName => "SniperPitchLimiter (AG2 parameter probe)";
    public override string ModuleVersion => "0.3.0";
    public override string ModuleAuthor => "Codex";
    public override string ModuleDescription => "Experimental AG2 aim-pitch parameter clamp; pose/hitbox validation pending.";
    public PitchConfig Config { get; set; } = new();
    // This image's aim-reflection function was inspected; never reuse these internals on another build.
    private const string ImageHash = "1F3FE3A814EAC5909B59B03D8464BC4808ADB9AD21F9BA1C6349F56F1664FA6D";
    private const string Signature = "55 48 89 E5 41 54 49 89 F4 53 48 89 FB 48 8D 7D D0 48 83 EC 30 C7 45 D8 00 00 00 00 F3 0F 7E 82 D4 03 00 00";
    private MemoryFunctionVoid<nint, nint, nint>? _reflectAim;
    private bool _hooked, _attempted;
    private volatile bool _faulted, _enabled;
    private int _pitchOffset;
    private long _calls, _changes;
    private volatile Dictionary<nint, Target> _targets = new();
    private readonly ConcurrentDictionary<int, Sample> _samples = new();
    private sealed record Target(int Slot, string Name, float Input, float Eye);
    private sealed record Sample(string Name, float Input, float Eye, float Before, float After, long Time);
    private static readonly HashSet<string> Snipers = new(StringComparer.Ordinal)
        { "weapon_awp", "weapon_ssg08", "weapon_scar20", "weapon_g3sg1" };

    public void OnConfigParsed(PitchConfig config)
    {
        if (!float.IsFinite(config.MaxDownPitch) || config.MaxDownPitch < 0 || config.MaxDownPitch > 89)
            throw new ArgumentException("MaxDownPitch must be between 0 and 89.");
        if (config.GraphDownSign != 1 && config.GraphDownSign != -1)
            throw new ArgumentException("GraphDownSign must be 1 or -1.");
        Config = config;
        _enabled = config.Enabled;
    }

    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnTick>(RefreshTargets);
        RegisterListener<Listeners.OnMapEnd>(() => { _targets = new(); _samples.Clear(); });
        Logger.LogInformation("AG2 probe 0.3.0 loaded, enabled={Enabled}; no eye/input angle writes.", _enabled);
    }

    private void RefreshTargets()
    {
        if (_faulted) return;
        try
        {
            if (!_attempted)
            {
                _attempted = true;
                if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Only the inspected Linux build is supported.");
                var image = Path.Combine(Server.GameDirectory, "bin", "linuxsteamrt64", "libserver.so");
                if (!File.Exists(image)) image = Path.Combine(Server.GameDirectory, "csgo", "bin", "linuxsteamrt64", "libserver.so");
                using var stream = File.OpenRead(image);
                if (Convert.ToHexString(SHA256.HashData(stream)) != ImageHash)
                    throw new InvalidOperationException("Server image changed; AG2 hook requires review. Nothing will be patched.");
                _pitchOffset = Schema.GetSchemaOffset("CCS2PawnGraphController", "m_flAimPitchAngle");
                if (_pitchOffset != 0x7b8) throw new InvalidOperationException("AG2 schema differs from the inspected image.");
                _reflectAim = new(Signature);
                if (_reflectAim.Handle == nint.Zero) throw new InvalidOperationException("AG2 function signature not found.");
                _reflectAim.Hook(OnReflectAim, HookMode.Post);
                _hooked = true;
                Logger.LogInformation("Verified Linux AG2 aim-reflection hook installed; enabled={Enabled}.", _enabled);
            }
            var next = new Dictionary<nint, Target>();
            foreach (var player in Utilities.GetPlayers())
            {
                if (!player.IsValid || player.IsHLTV || !player.PawnIsAlive) continue;
                var pawn = player.PlayerPawn.Value;
                if (pawn is null || !pawn.IsValid || pawn.Health <= 0) continue;
                if (Config.SnipersOnly)
                {
                    var weapon = pawn.WeaponServices?.ActiveWeapon.Value;
                    if (weapon is null || !weapon.IsValid || !Snipers.Contains(weapon.DesignerName)) continue;
                }
                next[pawn.Handle] = new(player.Slot, player.PlayerName, pawn.V_angle.X, pawn.EyeAngles.X);
            }
            // Immutable snapshot: native callbacks never call entity/schema APIs or iterate a mutable dictionary.
            _targets = next;
        }
        catch (Exception ex) { Fault(ex); }
    }

    private HookResult OnReflectAim(DynamicHook hook)
    {
        if (_faulted) return HookResult.Continue;
        try
        {
            Interlocked.Increment(ref _calls);
            var pawn = hook.GetParam<nint>(1);
            if (!_targets.TryGetValue(pawn, out var target)) return HookResult.Continue;
            var controller = hook.GetParam<nint>(0);
            // OptionalRef<float>: symbol +0, graph +8, int16 node index +16.
            // Same pointer chain and float store as this image's original aim-reflection function.
            var reference = controller + _pitchOffset;
            var graph = Marshal.ReadIntPtr(reference, 8);
            var index = Marshal.ReadInt16(reference, 16);
            if (graph == 0 || index < 0) return HookResult.Continue;
            var nodes = Marshal.ReadIntPtr(graph, 0x10);
            if (nodes == 0) return HookResult.Continue;
            var node = Marshal.ReadIntPtr(nodes, index * 8);
            if (node == 0) return HookResult.Continue;
            var valueAddress = node + 0x18;
            var before = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(valueAddress));
            if (!float.IsFinite(before) || MathF.Abs(before) > 181)
                throw new InvalidOperationException("AG2 aim parameter is not a finite angle; correction stopped.");
            var after = before;
            if (_enabled && before * Config.GraphDownSign > Config.MaxDownPitch)
            {
                after = Config.MaxDownPitch * Config.GraphDownSign;
                Marshal.WriteInt32(valueAddress, BitConverter.SingleToInt32Bits(after));
                Interlocked.Increment(ref _changes);
            }
            _samples[target.Slot] = new(target.Name, target.Input, target.Eye, before, after, Environment.TickCount64);
        }
        catch (Exception ex) { Fault(ex); }
        return HookResult.Continue;
    }

    private void Fault(Exception ex)
    {
        _enabled = false;
        if (!_faulted) Logger.LogError(ex, "AG2 probe disabled after an error.");
        _faulted = true;
    }

    private static bool CanControl(CCSPlayerController? player, CommandInfo command)
    {
        if (player is null || AdminManager.PlayerHasPermissions(player, "@css/root")) return true;
        command.ReplyToCommand("Server console or @css/root required.");
        return false;
    }

    [ConsoleCommand("css_pitch_status", "Show AG2 samples; pose/hitboxes require validation.")]
    public void Status(CCSPlayerController? player, CommandInfo command)
    {
        if (!CanControl(player, command)) return;
        command.ReplyToCommand($"AG2Probe v0.3.0 enabled={_enabled} hooked={_hooked} faulted={_faulted} limit={Config.MaxDownPitch} sign={Config.GraphDownSign} calls={Interlocked.Read(ref _calls)} corrections={Interlocked.Read(ref _changes)}. Pose/hitboxes UNVERIFIED.");
        foreach (var entry in _samples.OrderBy(x => x.Key))
        {
            var s = entry.Value;
            command.ReplyToCommand(FormattableString.Invariant($"slot={entry.Key} {s.Name}: input={s.Input:F1} eye={s.Eye:F1} AG2before={s.Before:F1} AG2after={s.After:F1} ageMs={Environment.TickCount64-s.Time}"));
        }
    }

    [ConsoleCommand("css_pitch_enable", "css_pitch_enable 0|1 (session only).")]
    public void Enable(CCSPlayerController? player, CommandInfo command)
    {
        if (!CanControl(player, command)) return;
        if (command.ArgCount != 2 || (command.GetArg(1) != "0" && command.GetArg(1) != "1"))
        { command.ReplyToCommand("Usage: css_pitch_enable 0|1"); return; }
        if (command.GetArg(1) == "1" && (_faulted || !_hooked))
        { command.ReplyToCommand("AG2 hook unavailable; inspect logs."); return; }
        _enabled = command.GetArg(1) == "1";
        command.ReplyToCommand($"AG2Probe enabled={_enabled} (session only); pose/hitboxes unverified.");
    }

    [ConsoleCommand("css_pitch_limit", "css_pitch_limit 0..89 (session only).")]
    public void Limit(CCSPlayerController? player, CommandInfo command)
    {
        if (!CanControl(player, command)) return;
        if (command.ArgCount != 2 || !float.TryParse(command.GetArg(1), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !float.IsFinite(value) || value < 0 || value > 89)
        { command.ReplyToCommand("Usage: css_pitch_limit 0..89"); return; }
        Config.MaxDownPitch = value;
        command.ReplyToCommand($"AG2Probe limit={value} (session only).");
    }

    [ConsoleCommand("css_pitch_sign", "css_pitch_sign -1|1 (session only).")]
    public void Sign(CCSPlayerController? player, CommandInfo command)
    {
        if (!CanControl(player, command)) return;
        if (command.ArgCount != 2 || !int.TryParse(command.GetArg(1), out var value) || (value != 1 && value != -1))
        { command.ReplyToCommand("Usage: css_pitch_sign -1|1"); return; }
        Config.GraphDownSign = value;
        command.ReplyToCommand($"AG2Probe downSign={value} (session only).");
    }

    public override void Unload(bool hotReload)
    {
        _enabled = false;
        _targets = new();
        if (_hooked) { _reflectAim!.Unhook(OnReflectAim, HookMode.Post); _hooked = false; }
        // The original routine rewrites the parameter on its next invocation.
    }
}
