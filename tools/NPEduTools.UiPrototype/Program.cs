using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace NPEduTools.UiPrototype;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            if (args.Length == 1 && args[0] == "--recording" || args.Length == 2 && args[0] == "--verify-recording")
            {
                var recording = new RecordingWorkspaceWindow();
                if (args.Length == 2)
                {
                    recording.Show(); recording.UpdateLayout();
                    RecordingPrototypeChecks.Run(recording, Path.GetFullPath(args[1])); recording.Close(); return 0;
                }
                recording.Closed += (_, _) => app.Shutdown(); app.Run(recording); return 0;
            }
            var window = new WorkspaceWindow();
            if (args.Length == 2 && args[0] == "--verify")
            {
                window.Show(); window.UpdateLayout();
                PrototypeChecks.Run(window, Path.GetFullPath(args[1])); window.Close(); return 0;
            }
            if (args.Length != 0) throw new ArgumentException("Usage: NPEduTools.UiPrototype [--recording | --verify-recording <output-directory> | --verify <output-directory>]");
            window.Closed += (_, _) => app.Shutdown(); app.Run(window); return 0;
        }
        catch (Exception error)
        {
            if (args.Length == 2) { Directory.CreateDirectory(args[1]); File.WriteAllText(Path.Combine(args[1], "error.txt"), error.ToString()); }
            else MessageBox.Show(error.Message, "交互原型", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
}
