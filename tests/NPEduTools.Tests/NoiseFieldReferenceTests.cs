using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.Tests;

public sealed class NoiseFieldReferenceTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-10-12T08:00:00Z");
    private static NoiseReferenceSample Point(string role,double digital,double reference) => new(Guid.NewGuid(),role,30,1,digital,digital-7.5,digital+7.5,0,"Good",
        Epoch.AddSeconds(role=="Primary"?5:40),Epoch.AddSeconds(role=="Primary"?35:70),reference,reference-7.5,reference+7.5,true,
        new(30,29,1,.01,.005,.995,0));
    private static NoiseReferenceDraft Draft(double error=0) => new(Guid.NewGuid(),"mic","DLY-2202","Slow",20,3,
        ReferenceNoiseMeter.AlgorithmVersion+"|synthetic",[Point("Primary",-40,50),Point("Check",-40+error,50)],"synthetic-usb","Field");

    [Theory]
    [InlineData(0d,false,true)] [InlineData(3d,false,true)] [InlineData(3.01,true,true)]
    [InlineData(5d,true,true)] [InlineData(5.001,false,false)] [InlineData(-5d,true,true)] [InlineData(-5.001,false,false)]
    public void Natural_fifteen_db_wave_does_not_require_six_db_separation_and_check_boundaries_remain_distinct(double error,bool warning,bool ready)
    {
        var result=NoiseReferenceCalibration.Evaluate(Draft(error));Assert.Equal(ready,result.Ready);
        if(ready){Assert.Equal(warning,result.Assessment!.WarningRequired);Assert.Equal("NearbyLevel",result.Assessment.Scope);Assert.Equal(90,result.OffsetDb);}
    }
    [Theory]
    [InlineData("clipping")][InlineData("coverage")][InlineData("short")][InlineData("overlap")]
    [InlineData("stale")][InlineData("range")][InlineData("unconfirmed")][InlineData("too-variable")]
    public void Field_mode_preserves_capture_timing_and_reference_quality_guards(string fault)
    {
        var d=Draft();var first=d.Samples[0];var second=d.Samples[1];
        first=fault switch {
            "clipping"=>first with {ClippedPercent=.101},"coverage"=>first with {Coverage=.89},"short"=>first with {Seconds=10},
            "stale"=>first with {MeterEvidence=first.MeterEvidence! with {MaximumGapSeconds=2}},
            "unconfirmed"=>first with {SameWindowConfirmed=false},"too-variable"=>first with {MinimumDbfs=-61},_=>first};
        second=fault switch {"overlap"=>second with {WindowStartedAt=first.WindowStartedAt,WindowEndedAt=first.WindowEndedAt},
            "range"=>second with {MeterEvidence=second.MeterEvidence! with {Range=1}},_=>second};
        Assert.False(NoiseReferenceCalibration.Evaluate(d with {Samples=[first,second]}).Ready);
    }
    [Fact]
    public void Field_requires_USB_and_stable_mode_keeps_its_old_limits_and_separation()
    {
        Assert.False(NoiseReferenceCalibration.Evaluate(Draft() with {MeterDevicePath=null}).Ready);
        Assert.False(NoiseReferenceCalibration.Evaluate(Draft() with {Workflow="Stable"}).Ready);
        Assert.False(NoiseReferenceContract.StabilityLimit(20.01,"Field"));Assert.False(NoiseReferenceContract.CheckLimit(3.01,"Field"));
        var command=new NoiseReferenceCommand("begin",Guid.NewGuid(),0,"test-reference-secret",Instrument:"DLY-2202",TimeWeighting:"Slow",StabilityDb:20,CheckToleranceDb:3,MeterDevicePath:"synthetic-usb",Workflow:"Field");
        Assert.True(NoiseReferenceContract.Valid(command));Assert.False(NoiseReferenceContract.Valid(command with {MeterDevicePath=null}));
        Assert.False(NoiseReferenceContract.Valid(command with {WarningAcknowledged=true}));
    }
    [Fact]
    public void Large_natural_reference_change_must_still_have_the_same_digital_direction()
    {
        var d=Draft();var check=Point("Check",-30,60);
        var good=NoiseReferenceCalibration.Evaluate(d with {Samples=[d.Samples[0],check]});Assert.True(good.Ready);Assert.Equal("TwoLevels",good.Assessment!.Scope);
        Assert.False(NoiseReferenceCalibration.Evaluate(d with {Samples=[d.Samples[0],Point("Check",-50,60)]}).Ready);
    }
    [Fact]
    public void Thirty_second_collector_uses_energy_and_never_finishes_at_the_old_ten_second_limit()
    {
        var collector=new NoiseReferenceCalibration("Primary","Slow",Epoch,"Field");
        for(int i=0;i<350;i++){
            double energy=Math.Pow(10,(-40+7.5*Math.Sin(i*Math.PI/50))/10);
            collector.Receive(new(.1,energy,energy,energy,.1,0,false,i>=50),(i+1)/10d);
            if(i==149)Assert.False(collector.Complete);
        }
        Assert.True(collector.Complete);var sample=collector.Finish(35,false);Assert.Equal("Good",sample.Quality);
        Assert.Equal(30,sample.Seconds,6);Assert.InRange(sample.MaximumDbfs!.Value-sample.MinimumDbfs!.Value,14.9,15.1);
        Assert.True(sample.DigitalDbfs>-40); // Energy mean is not the arithmetic mean of decibels.
    }
    [Fact]
    public void Field_USB_integration_excludes_warmup_and_uses_only_the_thirty_second_window()
    {
        var source=new NoiseUsbReference("Slow","Field");
        for(int i=0;i<40;i++)Assert.True(source.Receive(new(50+i%2*15,SoundMeterWeighting.A,SoundMeterTimeWeighting.Slow,false,false,0),i,i+.01,true));
        var sample=Point("Primary",-40,50);var result=source.Attach(sample,Epoch);
        Assert.True(NoiseReferenceContract.MeterEvidenceValid(result.MeterEvidence,"Field"));
        Assert.False(NoiseReferenceContract.MeterEvidenceValid(result.MeterEvidence));
        Assert.Equal(30,result.MeterEvidence!.Readings);Assert.Equal(50,result.ReferenceMinimumDbA);Assert.Equal(65,result.ReferenceMaximumDbA);
    }
}
