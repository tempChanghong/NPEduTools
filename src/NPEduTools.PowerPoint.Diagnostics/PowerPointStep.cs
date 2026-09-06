using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NPEduTools.PowerPoint.Diagnostics;

/// <summary>Explicit, single-shot COM experiment. Never called by the input observer.</summary>
[SupportedOSPlatform("windows")]
internal static class PowerPointStep
{
    public static StepResult Execute(Guid requestId)
    {
        bool dispatched = false;
        string stage = "InitialRead";
        ShowTarget? before = null;
        StepPlan? plan = null;
        var objects = new Stack<object>();
        object Keep(object value) { objects.Push(value); return value; }
        StepResult Result(string outcome, string reason, ShowTarget? after = null) =>
            new(requestId, DateTimeOffset.UtcNow, outcome, reason, plan, before, after);
        try
        {
            var reader = new PowerPointReader();
            var initial = reader.Read(true);
            if (initial.Status != "Showing" || initial.Windows.Length != 1) return Result("Refused", initial.Status);
            before = initial.Windows[0];
            stage = "Attach";
            var clsid = new Guid("91493441-5A91-11CF-8700-00AA0060263B");
            Native.GetActiveObject(ref clsid, 0, out var active);
            dynamic app = Keep(active);
            dynamic windows = Keep((object)app.SlideShowWindows);
            if ((int)windows.Count != 1) return Result("Refused", "ShowChanged");
            dynamic window = Keep((object)windows.Item(1));
            dynamic presentation = Keep((object)window.Presentation);
            if (reader.TargetId((string)presentation.FullName, (nint)before.Hwnd) != before.Id)
                return Result("Refused", "PresentationChanged");
            dynamic view = Keep((object)window.View);
            dynamic slide = Keep((object)view.Slide);
            if ((int)slide.SlideID != before.SlideId || (int)view.GetClickIndex() != before.ClickIndex)
                return Result("Refused", "StateChanged");
            dynamic settings = Keep((object)presentation.SlideShowSettings);
            dynamic slides = Keep((object)presentation.Slides);
            int slideCount = (int)slides.Count;
            stage = "ShowConfiguration";
            bool standardShow = (int)settings.RangeType == 1 && (int)settings.ShowType == 1 &&
                (int)settings.LoopUntilStopped == 0 && !Convert.ToBoolean((object)view.IsNamedShow);
            stage = "SlideSupport";
            bool simple = IsSimpleSlide((object)slide, Keep, before.ClickCount);
            int? nextId = null;
            bool nextSupported = false;
            if (before.SlideIndex < slideCount && before.ClickIndex == before.ClickCount)
            {
                dynamic next = Keep((object)slides.Item(before.SlideIndex + 1));
                nextId = (int)next.SlideID;
                nextSupported = IsSimpleSlide((object)next, Keep, null);
            }
            plan = StepPlan.Decide(before, simple, standardShow, slideCount, nextId, nextSupported);
            if (plan.Action == "Refuse") return Result("Refused", plan.Reason);
            if (plan.Action == "None") return Result("NoOp", plan.Reason);

            // Re-read the actual show rather than trusting an observation snapshot or queued touch.
            var fresh = reader.Read(true);
            if (fresh.Status != "Showing" || fresh.Windows.Length != 1 || !StepPlan.SameState(before, fresh.Windows[0]))
                return Result("Refused", "StateChanged");
            dynamic currentSlide = Keep((object)view.Slide);
            if ((int)currentSlide.SlideID != before.SlideId || (int)view.GetClickIndex() != before.ClickIndex ||
                (int)windows.Count != 1 || !IsSimpleSlide((object)currentSlide, Keep, before.ClickCount))
                return Result("Refused", "StateChanged");

            dispatched = true; // Any exception after this point has an uncertain external outcome.
            stage = "Dispatch";
            if (plan.Action == "Animation") view.GotoClick(plan.Click!.Value);
            else view.Next();

            var deadline = Stopwatch.GetTimestamp();
            stage = "Verification";
            ShowTarget? after = null;
            while (Stopwatch.GetElapsedTime(deadline).TotalMilliseconds < 1200)
            {
                Native.Pump();
                var snapshot = reader.Read();
                after = snapshot.Status == "Showing" && snapshot.Windows.Length == 1 ? snapshot.Windows[0] : null;
                if (after is not null && plan.MatchesResult(before, after)) return Result("Succeeded", "ExpectedStateObserved", after);
                if (after is null || !StepPlan.SameState(before, after)) break;
                Thread.Sleep(30);
            }
            return Result("Unknown", "ExpectedStateNotObserved", after);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return Result(dispatched ? "Unknown" : "Refused", $"{stage}:{error.GetType().Name}"); }
        finally
        {
            while (objects.TryPop(out var value))
                if (Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }
    }

    private static bool IsSimpleSlide(object slideObject, Func<object, object> keep, int? clickCount)
    {
        dynamic slide = slideObject;
        dynamic transition = keep((object)slide.SlideShowTransition);
        if ((int)transition.AdvanceOnTime != 0 || (int)transition.Hidden != 0 || (int)transition.EntryEffect != 0) return false;
        dynamic timeline = keep((object)slide.TimeLine);
        dynamic interactive = keep((object)timeline.InteractiveSequences);
        if ((int)interactive.Count != 0) return false;
        dynamic effects = keep((object)timeline.MainSequence);
        int count = (int)effects.Count;
        if (count > 64 || (clickCount.HasValue && count != clickCount.Value)) return false;
        for (int i = 1; i <= count; i++)
        {
            dynamic effect = keep((object)effects.Item(i));
            dynamic timing = keep((object)effect.Timing);
            // Only immediate Appear effects with one effect per page click are experimentally supported.
            if ((int)effect.EffectType != 1 || (int)timing.TriggerType != 1 || (float)timing.TriggerDelayTime != 0) return false;
        }
        dynamic links = keep((object)slide.Hyperlinks);
        if ((int)links.Count != 0) return false;
        dynamic shapes = keep((object)slide.Shapes);
        if ((int)shapes.Count > 64) return false;
        for (int i = 1; i <= (int)shapes.Count; i++)
        {
            dynamic shape = keep((object)shapes.Item(i));
            // Restrict the experimental fixture domain to ordinary auto-shapes and text boxes.
            if ((int)shape.Type is not (1 or 17)) return false;
            dynamic actions = keep((object)shape.ActionSettings);
            dynamic click = keep((object)actions.Item(1));
            dynamic hover = keep((object)actions.Item(2));
            if ((int)click.Action != 0 || (int)hover.Action != 0) return false;
        }
        return true;
    }
}
