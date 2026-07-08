namespace Tractus.Ndi.ConfigTui;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (!AppOptions.TryParse(args, Console.Error, out var options))
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(AppOptions.GetHelpText());
            return 2;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(AppOptions.GetHelpText());
            return 0;
        }

        if (options.PrintPath)
        {
            Console.WriteLine(options.ConfigPath);
            return 0;
        }

        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("This editor needs an interactive terminal. Use --help for command-line options.");
            return 2;
        }

        try
        {
            var document = NdiConfigDocument.Load(options.ConfigPath);
            var app = new AccessManagerApp(document, options.CreateBackup, options.Advanced);
            app.Run();
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            Console.ResetColor();
            Console.CursorVisible = true;
            Console.Error.WriteLine($"Could not open NDI config: {ex.Message}");
            return 1;
        }
    }
}
