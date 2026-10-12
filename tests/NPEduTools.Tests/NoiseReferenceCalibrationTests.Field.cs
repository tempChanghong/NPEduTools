using System.Text.Json;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Tests;

public sealed partial class NoiseReferenceCalibrationTests
{
    private static HostResponse FieldBegin(UsbFixture f)
    {
        var state=f.State;
        return f.Noise.ReferenceHandle(new(1,Guid.NewGuid(),"noise.reference.command",NoiseReference:
            new("begin",state.InstanceId,state.Revision,Secret,Instrument:"DLY-2202",TimeWeighting:"Slow",StabilityDb:20,CheckToleranceDb:3,MeterDevicePath:"synthetic-usb",Workflow:"Field")));
    }
    private static HostResponse FieldConfirm(UsbFixture f,bool warning)
    {
        var s=f.State;return f.Noise.ReferenceHandle(new(1,Guid.NewGuid(),"noise.reference.command",NoiseReference:
            new("confirm",s.InstanceId,s.Revision,Secret,s.Draft!.Id,WarningAcknowledged:warning)));
    }
    private static async Task FieldSample(UsbFixture f,string role,double digital,double reference)
    {
        f.Backend.Level=reference;await f.Start(role);await f.Frames(350,digital);await Until(()=>f.State.State=="Ready");
        Assert.Equal("Good",f.State.Draft!.Samples.Single(s=>s.Role==role).Quality);
    }
    [Theory]
    [InlineData(0d)][InlineData(4d)][InlineData(6d)]
    public async Task Field_real_Host_with_synthetic_HID_checks_warning_ack_persistence_and_same_level_reference(double error)
    {
        await using var f=new UsbFixture();Assert.Equal("Succeeded",FieldBegin(f).Outcome);
        await FieldSample(f,"Primary",-40,50);await FieldSample(f,"Check",-40+error,50);
        Assert.All(f.State.Draft!.Samples,s=>Assert.Equal(30,s.Seconds,6));
        if(error>5){Assert.Equal("REFERENCE_CHECK_FAILED",FieldConfirm(f,true).ErrorCode);Assert.Null(f.State.Profile);return;}
        if(error>3){Assert.Equal("REFERENCE_WARNING_ACK_REQUIRED",FieldConfirm(f,false).ErrorCode);Assert.Null(f.State.Profile);}
        Assert.Equal("Succeeded",FieldConfirm(f,error>3).Outcome);
        Assert.Equal(2,f.State.Profile!.Version);Assert.Equal("Field",f.State.Profile.Workflow);Assert.Equal(error>3,f.State.Profile.WarningAcknowledged);
        Assert.Contains("未验证全量程",NoiseReferenceCalibration.Describe(f.State.Profile));Assert.Equal(0,f.Reports);
        await using var reload=new NoiseService(_=>throw new Exception("No startup capture"),()=>[],f.Time,f.DirectoryPath,f.Management);
        var loaded=reload.ReferenceHandle(new(1,Guid.NewGuid(),"noise.reference.status")).NoiseReference!;
        Assert.Null(loaded.Error);Assert.Equal("Field",loaded.Profile!.Workflow);Assert.False(loaded.FreshSignatureMatched);
        if(error>3){
            var file=Path.Combine(f.DirectoryPath,"noise-reference-calibration.json");var node=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
            node["profile"]!["warningAcknowledged"]=false;File.WriteAllText(file,node.ToJsonString());
            await using var tampered=new NoiseService(_=>throw new Exception("No startup capture"),()=>[],f.Time,f.DirectoryPath,f.Management);
            var invalid=tampered.ReferenceHandle(new(1,Guid.NewGuid(),"noise.reference.status")).NoiseReference!;
            Assert.Equal("REFERENCE_STORE_UNAVAILABLE",invalid.Error);Assert.Null(invalid.Profile);
        }
    }
    [Theory]
    [InlineData("Disconnect")][InlineData("Gain")][InlineData("Cancel")][InlineData("Pause")][InlineData("Gap")]
    public async Task Field_interruption_releases_devices_and_cannot_be_confirmed(string fault)
    {
        await using var f=new UsbFixture();FieldBegin(f);await f.Start();await f.Frames(160);
        Assert.Equal("Sampling",f.State.State); // The original 20-second workflow cannot stop it here.
        switch(fault){case "Disconnect":f.Backend.DisconnectAt=f.Backend.Reads+1;break;case "Gain":f.Capture!.Signature=Signature+"|changed";break;
            case "Cancel":f.Command("cancel");break;case "Pause":f.Paused=true;break;case "Gap":f.Time.Advance(2);break;}
        await f.Frames(20);await Until(()=>f.Capture!.Disposed && f.Backend.Disposals==f.Backend.Opens);
        Assert.False(f.State.Evaluation.Ready);Assert.Null(f.State.Profile);Assert.NotEqual("Good",f.State.Draft!.Samples.Single().Quality);Assert.Equal(0,f.Reports);
    }
    [Fact]
    public async Task Restarting_first_field_segment_discards_the_old_check_and_keeps_confirmed_reference()
    {
        await using var f=new UsbFixture();FieldBegin(f);await FieldSample(f,"Primary",-40,50);await FieldSample(f,"Check",-40,50);
        FieldConfirm(f,false);var profile=f.State.Profile;await f.Start("Primary");
        Assert.Empty(f.State.Draft!.Samples);Assert.Same(profile,f.State.Profile);Assert.False(f.State.Evaluation.Ready);
        f.Command("cancel");await Until(()=>f.State.State=="Ready");Assert.Same(profile,f.State.Profile);
    }
    [Fact]
    public async Task Field_profile_drives_formal_estimates_thresholds_local_report_and_unchanged_KV_contract()
    {
        await using var f=new UsbFixture();FieldBegin(f);await FieldSample(f,"Primary",-40,50);await FieldSample(f,"Check",-40,50);FieldConfirm(f,false);
        var transport=new NoiseTransport(f.Noise,f.DirectoryPath);transport.Bind("synthetic-field-school");
        var id=Guid.NewGuid();Assert.Equal("Accepted",f.Noise.StartScheduled(id).Outcome);await Until(()=>f.Noise.Snapshot().State=="Active" && f.Capture is {Started:true,Disposed:false});
        var policy=new StudyNoise(true,-40,-50,600,3,new(65,50));f.Noise.StudyThresholds(id,policy);
        for(int i=0;i<60;i++){f.Time.Advance(.1);f.Capture!.EmitNormal();f.Capture.Emit(-35,true);}
        var state=f.Noise.Snapshot();Assert.Equal(55,state.Estimate!.CurrentDbA!.Value,5);Assert.Contains("未验证全量程",state.ReferenceNote);
        Assert.Equal(("Medium",false),f.Noise.StudyThresholds(id,policy));NpepNoiseProtocol.Validate("status",transport.Observe());
        f.Noise.StopScheduled(id);await Until(()=>!f.Noise.CaptureBusy);
        var report=f.Noise.StudyReportSnapshot()!;Assert.Contains("现场校准",report.ReferenceNote);Assert.Equal(55,report.Estimated!.EnergyMean!.Value,5);
        NpepNoiseProtocol.Validate("report",transport.Reports().Single()!.AsObject());
        var s=f.Noise.Snapshot();Assert.Equal("Accepted",f.Noise.StartScheduled(Guid.NewGuid()).Outcome);await Until(()=>f.Noise.Snapshot().State=="Active");
        f.Capture!.Signature=Signature+"|changed";f.Time.Advance(.1);f.Capture.EmitNormal();f.Capture.Emit(-35,true);
        Assert.Equal("INPUT_SETTINGS_CHANGED",f.Noise.Snapshot().Estimate!.Reason);Assert.Null(f.Noise.Snapshot().Estimate!.CurrentDbA);
    }
}
