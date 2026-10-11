using NPEduTools.Contracts;
using NPEduTools.Core;
namespace NPEduTools.Tests;
public sealed class StudyPresentationTimingTests
{
    private sealed class Clock : TimeProvider { public long Ms; public override long TimestampFrequency => 1000; public override long GetTimestamp() => Ms; }
    private static StudyHostState State(double seconds, string phase = "Preparing") {
        var start = new DateTimeOffset(2026,10,12,19,0,0,TimeSpan.Zero);
        return new("v","APPLIED","","",phase,"",true,start.AddSeconds(seconds),new(start,start.AddHours(1)),null,null,Display:new(Guid.NewGuid(),true,true,0));
    }
    [Fact] public void Five_minute_display_does_not_consume_two_minute_cue_and_late_entry_cannot_replay_it() {
        var c=new Clock();var t=new StudyPresentationTimeline(c);var s=State(-300);
        t.Apply(s);Assert.Null(t.Notice);Assert.Null(t.TakeCue());
        c.Ms=180000;t.Apply(s with{SchoolNow=s.Window!.Start.AddSeconds(-120)});Assert.Equal("Preparing",t.Notice);Assert.Equal("Preparing",t.TakeCue());
        c.Ms+=9000;t.Apply(s with{SchoolNow=s.Window!.Start.AddSeconds(-111)});Assert.Equal("Preparing",t.Notice);Assert.Null(t.TakeCue());
        c.Ms+=1000;t.Apply(s with{SchoolNow=s.Window!.Start.AddSeconds(-110)});Assert.Null(t.Notice);
        var late=new StudyPresentationTimeline(c);late.Apply(s with{SchoolNow=s.Window!.Start.AddSeconds(-60)});Assert.Null(late.Notice);Assert.Null(late.TakeCue());
    }
    [Fact] public void Short_manual_preparation_ends_exactly_at_start_then_has_independent_start_notice() {
        var c=new Clock();var t=new StudyPresentationTimeline(c);var s=State(-3);
        s=s with{Manual=new(Guid.NewGuid(),1,Guid.NewGuid(),"Preparing",null,null,null,true,600,3)};
        t.Apply(s);Assert.Equal("Preparing",t.TakeCue());
        c.Ms=3000;t.Apply(s with{SchoolNow=s.Window!.Start,Phase="Active"});Assert.Equal("Active",t.Notice);Assert.Equal("Active",t.TakeCue());
    }
    [Theory] [InlineData(-301,false)] [InlineData(-300,true)] [InlineData(119,true)] [InlineData(120,false)]
    public void Automatic_seat_bounds_never_extend_into_homework_rotation(int seconds,bool expected) {
        var s=State(seconds,seconds<0?"Preparing":"Active");var seat=new StudySeatingAutoDisplay();Assert.Equal(expected,seat.WantsImage(s));
        seat.ManualChoice();Assert.False(seat.WantsImage(s));Assert.True(seat.WantsImage(s with{Window=new(s.Window!.Start.AddDays(1),s.Window.End.AddDays(1)),SchoolNow=s.SchoolNow!.Value.AddDays(1).AddSeconds(-seconds-300),Phase="Preparing"}));
    }
    [Fact] public void Seat_choice_survives_transient_unavailability_and_policy_refresh_and_manual_has_no_auto_seat() {
        var s=State(-299);var a=new StudySeatingAutoDisplay();Assert.True(a.WantsImage(s));a.ManualChoice();
        Assert.False(a.WantsImage(s with{ClockReady=false}));Assert.False(a.WantsImage(s with{Version="new",Display=new(Guid.NewGuid(),true,true,0)}));
        Assert.False(a.WantsImage(s with{Window=new(s.Window!.Start,s.Window.End.AddMinutes(1))}));
        Assert.False(new StudySeatingAutoDisplay().WantsImage(s with{Manual=new(Guid.NewGuid(),1,Guid.NewGuid(),"Preparing",null,null,null,false,null)}));
    }
    [Fact] public void Completed_auto_seat_does_not_reopen_after_clock_moves_backwards() {
        var s=State(120,"Active");var seat=new StudySeatingAutoDisplay();Assert.False(seat.WantsImage(s));
        Assert.False(seat.WantsImage(s with{SchoolNow=s.Window!.Start.AddSeconds(30)}));
    }
    [Fact] public void Local_manual_preference_defaults_roundtrips_and_rejects_corrupt_input_without_overwrite() {
        var dir=Path.Combine(Path.GetTempPath(),"manual-prefs-"+Guid.NewGuid());Directory.CreateDirectory(dir);var file=Path.Combine(dir,"settings.json");var store=new ManualStudyPreferencesStore(file);
        try {Assert.Equal(120,store.Read().DisplayLeadSeconds);store.Save(new(DisplayLeadSeconds:300));Assert.Equal(300,store.Read().DisplayLeadSeconds);
            File.WriteAllText(file,"{\"DisplayLeadSeconds\":-1}");Assert.Throws<InvalidDataException>(()=>store.Read());Assert.Contains("-1",File.ReadAllText(file));
            Assert.Throws<InvalidDataException>(()=>store.Save(new(DisplayLeadSeconds:1801)));Assert.Contains("-1",File.ReadAllText(file));
        }finally{File.Delete(file);Directory.Delete(dir);}
    }
}
