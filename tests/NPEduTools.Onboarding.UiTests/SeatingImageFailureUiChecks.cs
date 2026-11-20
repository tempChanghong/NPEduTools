using System.IO;
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
        string source = Path.Combine(folder, "valid.png");
        string broken = Path.Combine(folder, "broken.png");
        string replacement = Path.Combine(folder, "replacement.png");
        File.WriteAllBytes(source, valid);
        File.WriteAllBytes(broken, malformed);
        File.WriteAllBytes(replacement, SeatImage(Colors.LightBlue));
        var window = new StudySeatingUploadWindow("NPEduTools.Test.seating-image." + Guid.NewGuid().ToString("N"));
        void Complete(Task pending)
        { PumpUntil(() => pending.IsCompleted, "image fixture read did not finish"); pending.GetAwaiter().GetResult(); }
        try
        {
            // Never show/load the window: no Host request, upload or physical device access.
            Complete(window.LoadDraftAsync(source));
            var preview = (Image)window.FindName("Preview");
            var draftInfo = (TextBlock)window.FindName("DraftInfo");
            var heading = (TextBlock)window.FindName("ResultHeading");
            var result = (TextBlock)window.FindName("ResultText");
            var original = preview.Source;
            string originalInfo = draftInfo.Text;
            Assert(original is not null && originalInfo.Contains("valid.png"), "fixture did not identify a valid draft");
            var loading = window.LoadDraftAsync(broken);
            PumpUntil(() => loading.IsCompleted, "damaged image did not settle");
            Assert(!loading.IsFaulted, "decoder exception escaped the upload task: " + loading.Exception?.GetBaseException().GetType().FullName);
            loading.GetAwaiter().GetResult();
            Assert(ReferenceEquals(preview.Source, original) && draftInfo.Text == originalInfo,
                "failed image load replaced the previous preview or relabeled it as the failed file");
            Assert(heading.Text == "图片未载入" && result.Text.Contains("无法读取图片") && result.Text.Contains("上一张图片"),
                "damaged image feedback failed to identify the retained preview as the previous image");
            Assert(Button(window, "ChooseImage").IsEnabled && Button(window, "RefreshScope").IsEnabled &&
                !Button(window, "SaveImage").IsEnabled, "damaged image left recovery blocked or allowed an unconfirmed upload");
            Assert(File.ReadAllBytes(broken).SequenceEqual(malformed), "failed decoder modified the source image");
            Complete(window.LoadDraftAsync(replacement));
            Assert(preview.Source is not null && !ReferenceEquals(preview.Source, original) &&
                draftInfo.Text.Contains("replacement.png") && result.Text.Contains("请核对图片"),
                "a valid image did not recover after decoder failure");
            Checks.Add("Seating image/" + name + ": corrupt PNG identifies the retained preview and filename as the previous image, preserves source bytes, and accepts a different valid draft; no Host/upload/sensors");
        }
        finally { window.Close(); }
    }
}
