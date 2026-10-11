using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Core;

internal static partial class Program
{
    private static void RunStudyHomeworkVisualChecks()
    {
        var clock = new StudyUiClock(); var surface = new StudySurface(() => Task.CompletedTask, () => { }, true, clock);
        var owner = new Window { Content = surface, Width = 1440, Height = 810, WindowStyle = WindowStyle.None };
        var state = StudyFixture() with { Homework = new("READY", new string('a',64), 1, "2026-10-12", new string('a',64)) };
        StudyHomeworkItem Work(string id, string content) => new(id, 1, "物理", "", content, [new("one", "行政一班"),new("walk", "物理走班 A")]);
        var items = Enumerable.Range(1,7).Select(i => Work("work"+i,$"第 {i} 页，完成实验题。")).ToArray();
        T Element<T>(string name) where T : FrameworkElement => (T)surface.FindName(name);
        var pager = (StudyHomeworkPager)typeof(StudySurface).GetField("_pager",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(surface)!;
        void Apply() => surface.Apply(state, null);
        try
        {
            Exercise(owner, window =>
            {
                Apply(); surface.ApplyHomework(state.Homework!.SnapshotId!,new(state.Version!,"2026-10-12",items));
                Assert(Element<Grid>("HomeworkGrid").Children.Count==6 && Element<TextBlock>("PageLabel").Text=="1 / 2","short homework does not form measured six-card pages");
                Snapshot(window,"study-homework.png");
                clock.Ms+=59000; Apply(); Assert(pager.Page==0,"homework changed before one minute");
                state=state with { Homework=state.Homework with { SnapshotId=new string('b',64) } }; Apply();
                surface.ApplyHomework(state.Homework.SnapshotId!,new(state.Version!,"2026-10-12",[Work("updated","更新后的作业。" )]));
                Assert(Element<TextBlock>("WorkUpdateLabel").Text.Contains("本页读完"),"update did not wait for current page");
                clock.Ms+=1000; Apply(); Assert(Element<TextBlock>("PageLabel").Text=="" && Element<Grid>("HomeworkGrid").Children.Count==1,"pending update did not commit at reading boundary");
                state=state with { Homework=state.Homework with { SnapshotId=new string('c',64) } }; Apply();
                string longText=string.Concat(Enumerable.Repeat("完成实验题：分析现象并说明原因，列出条件。\n",65));
                surface.ApplyHomework(state.Homework.SnapshotId!,new(state.Version!,"2026-10-12",[Work("long",longText)]));
                clock.Ms+=60000; Apply();
                Assert(pager.PageCount>1 && Element<Grid>("HomeworkGrid").ColumnDefinitions.Count==2,"long content was shrunk instead of paginated");
                var allPages=(StudyHomeworkPage[])typeof(StudySurface).GetField("_pages",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(surface)!;
                Assert(string.Concat(allPages.SelectMany(p=>p.Items).Select(p=>p.Text))==longText,"measured pagination lost source text");
                Snapshot(window,"study-homework-long.png");
                foreach(var child in Element<Grid>("HomeworkGrid").Children.OfType<StackPanel>())
                { child.Measure(new Size(880,302)); Assert(child.DesiredSize.Height<=302,"long homework overflows card height"); }
                int before=pager.Page; surface.Suspend(); clock.Ms+=180000; Apply();
                Assert(pager.Page==before && pager.RemainingSeconds==60,"restoring display consumed hidden reading time");
                // Advance to a distinct upcoming window; rewinding the same window must not replay its start cue.
                state=state with { Window=new(state.Window!.Start.AddMinutes(20),state.Window.End) };
                state=state with { Phase="Preparing",SchoolNow=state.Window.Start.AddSeconds(-90) }; Apply();clock.Ms+=90000;
                state=state with { Phase="Active",SchoolNow=state.Window!.Start };Apply();clock.Ms+=5000;
                state=state with { SchoolNow=state.Window!.Start.AddSeconds(5) };Apply();
                Assert(pager.Page==before && pager.RemainingSeconds==60,"preparation/start overlay consumed homework reading time");
                state=state with { SchoolNow=state.SchoolNow!.Value.AddDays(1) };Apply();
                Assert(Element<Grid>("HomeworkGrid").Children.OfType<TextBlock>().Any(t=>t.Text.Contains("待同步")),"old homework survived school calendar date change");
                Checks.Add("Study homework: actual WPF measurement, source labels, long text preserved, 60-second rotation, deferred update, hide/preparation/start reading pause, next-day fencing; synthetic content only");
                window.Close();
            });
        }
        finally { surface.Stop(); owner.Close(); }
    }
}
