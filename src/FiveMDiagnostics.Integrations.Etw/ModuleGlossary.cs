namespace FiveMDiagnostics.Integrations.Etw;

/// <summary>
/// Names the handful of modules that keep turning up in the traces, so the line reads without someone
/// running the offline analyser by hand.
/// </summary>
/// <remarks>
/// <para>
/// The attribution line already said <c>54 % adhesive.dll</c> for the busiest thread of every capture in
/// the 29 August session, and that string is what made the finding possible — but it took a search to
/// learn that <c>adhesive.dll</c> is FiveM's own anti-tamper layer, and therefore that the game's single
/// most expensive thread all evening was its anti-cheat rather than anything to do with rendering. It
/// held 0.44 to 0.56 cores continuously and burst to 3.58 across four threads during one 252 ms hitch,
/// blocking the main thread for 71 ms while a server script decoded an image through it.
/// </para>
/// <para>
/// Deliberately short. This is not a symbol server and it is not a list of every DLL on Windows; it is
/// the six or seven names that decided an investigation, and each one earns its line by having been
/// looked up during it. A module not in the list is printed as it comes, which is what the line did
/// before.
/// </para>
/// </remarks>
internal static class ModuleGlossary
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["adhesive.dll"] = "FiveM:s anti-cheat",
        ["nvlddmkm.sys"] = "NVIDIA kärndrivrutin",
        ["nvwgf2umx.dll"] = "NVIDIA D3D-drivrutin",
        ["dxgkrnl.sys"] = "DirectX-kärnschemaläggare",
        ["dxgmms2.sys"] = "DirectX minneshanterare",
        ["win32kfull.sys"] = "fönster- och GDI-vägen",
        ["win32kbase.sys"] = "fönster- och GDI-vägen",
        ["win32k.sys"] = "fönster- och GDI-vägen",
        ["ntoskrnl.exe"] = "Windows-kärnan, ospecificerad",
        ["citizen-scripting-lua.dll"] = "FiveM Lua-skript",
        ["libcef.dll"] = "spelets inbyggda webbläsare",
    };

    /// <summary>Returns the module name with its role appended, or unchanged when it has no entry.</summary>
    public static string Annotate(string module)
    {
        return Names.TryGetValue(module, out var role) ? $"{module} ({role})" : module;
    }
}
