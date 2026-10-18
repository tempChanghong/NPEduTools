using System.IO;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Media;
using NPEduTools.App;

internal static partial class Program
{
    private static void RunSeatingImageFailureChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Application.Current.Dispatcher));
        try
        {
            byte[] valid = SeatImage(Colors.White);
            var inputs = new Dictionary<string, byte[]>
            {
                ["signature-only"] = valid[..8],
                ["incomplete-header"] = valid[..24],
                ["header-only"] = valid[..33],
            };
            var failures = new List<Exception>();
            foreach (var (name, bytes) in inputs)
                try { SeatingImageFailureCheck(name, bytes, valid); }
                catch (Exception error) { failures.Add(new InvalidOperationException(name + ": " + error.Message, error)); }
            if (failures.Count > 0) throw new AggregateException(failures);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void SeatingImageFailureCheck(string name, byte[] malformed, byte[] valid)
    {
        string folder = Path.Combine(_output, "seating-image-" + name); Directory.CreateDirectory(folder);
        string source = Path.Combine(folder, "valid.png"), broken = Path.Combine(folder, "broken.png");
        File.WriteAllBytes(source, valid); File.WriteAllBytes(broken, malformed);
        var window = new StudySeatingUploadWindow("NPEduTools.Test.seating-image." + Guid.NewGuid().ToString("N"));
        Task Load(string path) => (Task)typeof(StudySeatingUploadWindow).GetMethod("LoadDraftAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [path])!;
        void Complete(Task pending)
        { PumpUntil(() => pending.IsCompleted, "image fixture read did not finish"); pending.GetAwaiter().GetResult(); }
        try
        {
            // Never show/load the window: no Host request, upload or physical device access.
            Complete(Load(source));
            var preview = (Image)window.FindName("Preview");
            var original = preview.Source;
            Assert(original is not null, "fixture did not load a valid draft");
            var loading = Load(broken);
            PumpUntil(() => loading.IsCompleted, "damaged image did not settle");
            Assert(!loading.IsFaulted, "decoder exception escaped the upload task: " + loading.Exception?.GetBaseException().GetType().FullName);
            loading.GetAwaiter().GetResult();
            Assert(ReferenceEquals(preview.Source, original), "failed image load replaced the previous preview");
            Assert(((TextBlock)window.FindName("ResultText")).Text.Contains("无法读取图片") &&
                ((Button)window.FindName("ChooseImage")).IsEnabled && ((Button)window.FindName("RefreshScope")).IsEnabled,
                "damaged image lacks recoverable feedback or leaves controls busy");
            Assert(File.ReadAllBytes(broken).SequenceEqual(malformed), "failed decoder modified the source image");
            Complete(Load(source));
            Assert(preview.Source is not null && ((TextBlock)window.FindName("ResultText")).Text.Contains("请核对图片"),
                "a valid image did not recover after decoder failure");
            Checks.Add("Seating image/" + name + ": corrupt PNG fails without escaping the upload UI, retains previous preview/source bytes, releases busy state and accepts valid retry; no Host/upload/sensors");
        }
        finally { window.Close(); }
    }
}
