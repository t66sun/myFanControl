using FanControl.Core;

int checks=0;
void Check(bool value,string message){checks++;if(!value)throw new InvalidOperationException(message);}
void Reject(Action action,string message){try{action();}catch(ArgumentException){checks++;return;}throw new InvalidOperationException(message);}
// Values below are synthetic verification data, not an activated 21CX hardware profile.
var limits=new RpmLimits(1500,7500);
CurvePoint[] points=[new(40,1500),new(60,3500),new(80,7500)];
var curve=new FanCurve(points,limits);
points[0]=new(40,7500);
Check(curve.Evaluate(40)==1500,"Caller mutated the curve");
Check(curve.Evaluate(0)==1500 && curve.Evaluate(100)==7500,"Endpoint limits");
Check(curve.Evaluate(50)==2500 && curve.Evaluate(70)==5500,"Interpolation");
Reject(()=>new FanCurve([new(40,2000),new(40,7500)],limits),"Duplicate temperatures accepted");
Reject(()=>new FanCurve([new(40,3000),new(60,2000),new(80,7500)],limits),"Descending RPM accepted");
Reject(()=>new FanCurve([new(40,1500),new(80,7000)],limits),"Maximum endpoint missing");
Reject(()=>curve.Evaluate(double.NaN),"NaN curve input accepted");
Reject(()=>new FanCurve([new(40,1500),new(double.PositiveInfinity,7500)],limits),"Infinite point accepted");
var policy=new FanControlPolicy(curve,["CPU","GPU"],TimeSpan.FromSeconds(3),TimeSpan.FromSeconds(1),2,95,500);
var now=DateTimeOffset.Parse("2026-09-30T00:00:10Z");
TemperatureFrame Frame(double? cpu,double? gpu,DateTimeOffset? at=null)=>new(at??now,new Dictionary<string,double?>{{"CPU",cpu},{"GPU",gpu}});
var first=policy.Evaluate(Frame(50,70),now,null);
Check(first.Action==ControlAction.RequestRpm && first.RequestedRpm==5500,"Hottest required sensor was not used");
var applied=new AppliedControl(5500,70,now-TimeSpan.FromSeconds(2));
var cool=policy.Evaluate(Frame(60,60),now,applied);
Check(cool.RequestedRpm==4500 && cool.Reason==DecisionReason.Cooling,"Cooling slew exceeded 500 RPM/sec");
var hysteresis=policy.Evaluate(Frame(69,65),now,applied);
Check(hysteresis.Action==ControlAction.Hold && hysteresis.RequestedRpm is null && hysteresis.Reason==DecisionReason.CoolingHysteresis,"Cooling deadband");
var recent=applied with {AppliedAtUtc=now-TimeSpan.FromMilliseconds(100)};
Check(policy.Evaluate(Frame(75,70),now,recent).Reason==DecisionReason.WriteInterval,"Write interval");
var emergency=policy.Evaluate(Frame(96,70),now,recent);
Check(emergency.Action==ControlAction.RequestRpm && emergency.RequestedRpm==7500 && emergency.Reason==DecisionReason.HighTemperature,"High temperature blocked by write interval");
Check(policy.Evaluate(Frame(75,70),now,applied).RequestedRpm==6500,"Heating was delayed by cooling limit");
Check(policy.Evaluate(Frame(70,65),now,applied).Action==ControlAction.Hold,"Unnecessary repeated RPM request");
foreach(var frame in new[]{Frame(null,70),Frame(double.NaN,70),Frame(60,double.PositiveInfinity),Frame(60,70,now-TimeSpan.FromSeconds(4)),Frame(60,70,now+TimeSpan.FromSeconds(1)),new TemperatureFrame(now,new Dictionary<string,double?>{{"CPU",60}})})
{
    var d=policy.Evaluate(frame,now,applied);
    Check(d.Action==ControlAction.RestoreFirmwareAutomatic && d.RequestedRpm is null,"Bad sensor input produced manual RPM");
}
foreach(var history in new[]{applied with {Rpm=8000},applied with {TemperatureCelsius=double.NaN},applied with {AppliedAtUtc=now+TimeSpan.FromSeconds(1)}})
    Check(policy.Evaluate(Frame(60,70),now,history).Reason==DecisionReason.InvalidHistory,"Invalid applied state accepted");
// Evaluating a proposal must not pretend a hardware write succeeded.
Check(policy.Evaluate(Frame(75,70),now,applied)==policy.Evaluate(Frame(75,70),now,applied),"Policy mutated confirmed state");
var slowPolicy=new FanControlPolicy(curve,["CPU","GPU"],TimeSpan.FromSeconds(3),TimeSpan.FromMilliseconds(50),2,95,1);
var fractionalCooling=slowPolicy.Evaluate(Frame(60,60),now,applied with {AppliedAtUtc=now-TimeSpan.FromMilliseconds(200)});
Check(fractionalCooling.Action==ControlAction.Hold && fractionalCooling.RequestedRpm is null && fractionalCooling.Reason==DecisionReason.CoolingRateLimit,"Sub-RPM cooling step repeated a setting");
Check(policy.Evaluate(Frame(60,70,now-TimeSpan.FromSeconds(3)),now,applied).Action!=ControlAction.RestoreFirmwareAutomatic,"Age boundary rejected a current-enough sample");
Console.WriteLine($"{checks} control-policy checks passed. Synthetic inputs only; no hardware control or validated laptop RPM profile.");
