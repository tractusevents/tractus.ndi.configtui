namespace Tractus.Ndi.ConfigTui;

internal sealed record AppOptions(
    string ConfigPath,
    bool CreateBackup,
    bool PrintPath,
    bool ShowHelp,
    bool Advanced)
{
    public const string ConfigFileName = "ndi-config.v1.json";

    public static bool TryParse(string[] args, TextWriter error, out AppOptions options)
    {
        string? file = null;
        string? configDir = null;
        var forceUserConfig = false;
        var createBackup = true;
        var printPath = false;
        var showHelp = false;
        var advanced = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    showHelp = true;
                    break;
                case "-f":
                case "--file":
                    if (!TryReadValue(args, ref i, arg, error, out file))
                    {
                        options = Empty;
                        return false;
                    }

                    break;
                case "--config-dir":
                    if (!TryReadValue(args, ref i, arg, error, out configDir))
                    {
                        options = Empty;
                        return false;
                    }

                    break;
                case "--user":
                    forceUserConfig = true;
                    break;
                case "--no-backup":
                    createBackup = false;
                    break;
                case "--print-path":
                    printPath = true;
                    break;
                case "--advanced":
                    advanced = true;
                    break;
                default:
                    error.WriteLine($"Unknown option: {arg}");
                    options = Empty;
                    return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(file) && !string.IsNullOrWhiteSpace(configDir))
        {
            error.WriteLine("Use either --file or --config-dir, not both.");
            options = Empty;
            return false;
        }

        var path = !string.IsNullOrWhiteSpace(file)
            ? ExpandPath(file)
            : ResolveDefaultConfigPath(configDir, forceUserConfig);

        options = new AppOptions(Path.GetFullPath(path), createBackup, printPath, showHelp, advanced);
        return true;
    }

    public static string GetHelpText() =>
        """
        Config Editor for NDI | Tractus Events

        Usage:
          ndi-config [options]

        Options:
          -f, --file <path>       Edit an arbitrary NDI JSON config file.
              --config-dir <dir>  Edit <dir>/ndi-config.v1.json.
              --user              Force the current user's $HOME/.ndi config.
              --no-backup         Do not write a .bak file before saving.
              --print-path        Print the resolved config path and exit.
              --advanced          Show Advanced SDK codec and vendor settings.
          -h, --help              Show this help.

        Keys:
          Up/Down                  Move menu selection.
          Left/Right or Tab        Move action-button focus.
          Mouse click              Select menu rows and action buttons.
          Enter or Space           Activate the focused button or selected row.
          1-9                      Select the numbered menu item.
          A                        Add an external source in that menu.
          D or Delete              Delete the selected external source.
          B, Backspace, or Esc     Go back; Esc exits from the main menu.
          Ctrl+S or F10            Apply changes to disk.
          F1 or ?                  Show in-app key help.
        """;

    private static readonly AppOptions Empty = new(string.Empty, CreateBackup: true, PrintPath: false, ShowHelp: false, Advanced: false);

    private static bool TryReadValue(string[] args, ref int index, string option, TextWriter error, out string? value)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith("-", StringComparison.Ordinal))
        {
            error.WriteLine($"{option} requires a value.");
            value = null;
            return false;
        }

        index++;
        value = args[index];
        return true;
    }

    private static string ResolveDefaultConfigPath(string? configDir, bool forceUserConfig)
    {
        if (!string.IsNullOrWhiteSpace(configDir))
        {
            return Path.Combine(ExpandPath(configDir), ConfigFileName);
        }

        if (!forceUserConfig)
        {
            var ndiConfigDir = Environment.GetEnvironmentVariable("NDI_CONFIG_DIR");
            if (!string.IsNullOrWhiteSpace(ndiConfigDir))
            {
                return Path.Combine(ExpandPath(ndiConfigDir), ConfigFileName);
            }
        }

        return Path.Combine(GetHomeDirectory(), ".ndi", ConfigFileName);
    }

    private static string GetHomeDirectory()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            return home;
        }

        home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            return home;
        }

        throw new InvalidOperationException("Could not resolve the current user's home directory.");
    }

    private static string ExpandPath(string path)
    {
        if (path == "~")
        {
            return GetHomeDirectory();
        }

        if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            return Path.Combine(GetHomeDirectory(), path[2..]);
        }

        return path;
    }
}
