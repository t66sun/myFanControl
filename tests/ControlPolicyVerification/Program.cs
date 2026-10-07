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
var delayed=new FanControlPolicy(curve,["CPU","GPU"],TimeSpan.FromSeconds(3),TimeSpan.FromSeconds(1),2,95,500,TimeSpan.FromSeconds(1));
var baseline=new AppliedControl(3500,60,now-TimeSpan.FromSeconds(5));
Check(delayed.Evaluate(Frame(70,60),now,baseline).Reason==DecisionReason.HeatingDelay,"Heating did not start the delay");
Check(delayed.Evaluate(Frame(70,60,now+TimeSpan.FromMilliseconds(900)),now+TimeSpan.FromMilliseconds(900),baseline).Reason==DecisionReason.HeatingDelay,"Heating rose before one second");
Check(delayed.Evaluate(Frame(70,60,now+TimeSpan.FromSeconds(1)),now+TimeSpan.FromSeconds(1),baseline).RequestedRpm==5500,"Sustained heating did not rise after one second");
var interrupted=new FanControlPolicy(curve,["CPU","GPU"],TimeSpan.FromSeconds(3),TimeSpan.FromSeconds(1),2,95,500,TimeSpan.FromSeconds(1));
Check(interrupted.Evaluate(Frame(70,60),now,baseline).Reason==DecisionReason.HeatingDelay,"Interrupted heating did not start");
Check(interrupted.Evaluate(Frame(60,60,now+TimeSpan.FromMilliseconds(500)),now+TimeSpan.FromMilliseconds(500),baseline).Action==ControlAction.Hold,"Cooling to the applied target was delayed");
Check(interrupted.Evaluate(Frame(70,60,now+TimeSpan.FromMilliseconds(750)),now+TimeSpan.FromMilliseconds(750),baseline).Reason==DecisionReason.HeatingDelay,"A brief temperature dip did not restart the delay");
Check(interrupted.Evaluate(Frame(70,60,now+TimeSpan.FromMilliseconds(1500)),now+TimeSpan.FromMilliseconds(1500),baseline).Reason==DecisionReason.HeatingDelay,"The old heating timer survived a temperature dip");
Check(interrupted.Evaluate(Frame(96,60,now+TimeSpan.FromMilliseconds(1600)),now+TimeSpan.FromMilliseconds(1600),baseline).RequestedRpm==7500,"High-temperature protection was delayed");
var missingReset=new FanControlPolicy(curve,["CPU","GPU"],TimeSpan.FromSeconds(3),TimeSpan.FromSeconds(1),2,95,500,TimeSpan.FromSeconds(1));
_ = missingReset.Evaluate(Frame(70,60),now,baseline);
Check(missingReset.Evaluate(Frame(null,60,now+TimeSpan.FromMilliseconds(500)),now+TimeSpan.FromMilliseconds(500),baseline).Action==ControlAction.RestoreFirmwareAutomatic,"Missing sensor did not restore firmware");
Check(missingReset.Evaluate(Frame(70,60,now+TimeSpan.FromSeconds(1)),now+TimeSpan.FromSeconds(1),baseline).Reason==DecisionReason.HeatingDelay,"Missing sensor did not reset the heating delay");
Check(delayed.Evaluate(Frame(60,60),now,new AppliedControl(5500,70,now-TimeSpan.FromSeconds(5))).Reason==DecisionReason.Cooling,"Cooling was delayed");
Reject(()=>new FanControlPolicy(curve,["CPU"],TimeSpan.FromSeconds(3),TimeSpan.FromSeconds(1),2,95,500,TimeSpan.FromSeconds(11)),"Excessive heating delay accepted");
var offsetPolicy=new FanControlPolicy(curve,["CPU","GPU"],TimeSpan.FromSeconds(3),TimeSpan.FromSeconds(1),5,95,500,
    TimeSpan.FromSeconds(2),heatingHysteresis:3);
Check(offsetPolicy.Evaluate(Frame(62,60),now,baseline).Reason==DecisionReason.HeatingHysteresis,"Heating inside the offset must hold without starting the timer");
Check(offsetPolicy.Evaluate(Frame(63,60,now.AddSeconds(1)),now.AddSeconds(1),baseline).Reason==DecisionReason.HeatingDelay,"Offset boundary must start the heating timer");
Check(offsetPolicy.Evaluate(Frame(63,60,now.AddSeconds(2)),now.AddSeconds(2),baseline).Action==ControlAction.Hold,"Heating timer started before the offset boundary");
Check(offsetPolicy.Evaluate(Frame(64,60,now.AddSeconds(3)),now.AddSeconds(3),baseline).RequestedRpm==4300,"Sustained heating must use the latest curve target");
var coolingDelayed=new FanControlPolicy(curve,["CPU","GPU"],TimeSpan.FromSeconds(3),TimeSpan.FromSeconds(1),5,95,500,
    TimeSpan.FromSeconds(2),heatingHysteresis:3,coolingDelay:TimeSpan.FromSeconds(2));
var hotBaseline=new AppliedControl(5500,70,now.AddSeconds(-5));
Check(coolingDelayed.Evaluate(Frame(65,60),now,hotBaseline).Reason==DecisionReason.CoolingDelay,"Cooling boundary must start its own timer");
Check(coolingDelayed.Evaluate(Frame(66,60,now.AddSeconds(1)),now.AddSeconds(1),hotBaseline).Reason==DecisionReason.CoolingHysteresis,"Leaving the cooling threshold must cancel the wait");
Check(coolingDelayed.Evaluate(Frame(65,60,now.AddSeconds(2)),now.AddSeconds(2),hotBaseline).Reason==DecisionReason.CoolingDelay,"Cooling delay did not restart");
Check(coolingDelayed.Evaluate(Frame(64,60,now.AddSeconds(3)),now.AddSeconds(3),hotBaseline).Action==ControlAction.Hold,"Cooling used an interrupted timer");
Check(coolingDelayed.Evaluate(Frame(64,60,now.AddSeconds(4)),now.AddSeconds(4),hotBaseline).RequestedRpm==4300,"Sustained cooling did not use the latest target");
Check(coolingDelayed.Evaluate(Frame(96,60),now,hotBaseline).RequestedRpm==7500,"High temperature did not bypass offsets and both timers");
Check(coolingDelayed.Evaluate(Frame(70,60),now,null).RequestedRpm==5500,"First application must bypass all response settings");
Reject(()=>new FanControlPolicy(curve,["CPU"],TimeSpan.FromSeconds(3),TimeSpan.FromSeconds(1),2,95,500,heatingHysteresis:double.NaN),"Invalid heating offset accepted");
var newBaseline=new AppliedControl(4300,64,now.AddSeconds(3));
Check(offsetPolicy.Evaluate(Frame(67,60,now.AddSeconds(4)),now.AddSeconds(4),newBaseline).Reason==DecisionReason.HeatingDelay,"A successful change must establish a new offset baseline and timer");
Check(offsetPolicy.Evaluate(Frame(67,60,now.AddSeconds(5)),now.AddSeconds(5),newBaseline).Action==ControlAction.Hold,"Previous baseline timer survived a successful change");
Check(offsetPolicy.Evaluate(Frame(67,60,now.AddSeconds(6)),now.AddSeconds(6),newBaseline).RequestedRpm==4900,"New baseline could not finish its own waiting period");
_ = coolingDelayed.Evaluate(Frame(65,60,now.AddSeconds(6)),now.AddSeconds(6),hotBaseline);
Check(coolingDelayed.Evaluate(Frame(74,60,now.AddSeconds(7)),now.AddSeconds(7),hotBaseline).Reason==DecisionReason.HeatingDelay,"Direction reversal did not start a fresh heating timer");
Check(coolingDelayed.Evaluate(Frame(65,60,now.AddSeconds(8)),now.AddSeconds(8),hotBaseline).Reason==DecisionReason.CoolingDelay,"Returning to cooling used its old timer");
Check(coolingDelayed.Evaluate(Frame(65,60,now.AddSeconds(9)),now.AddSeconds(9),hotBaseline).Action==ControlAction.Hold,"Cooling did not wait after a direction reversal");
Console.WriteLine($"{checks} control-policy checks passed. Synthetic inputs only; no hardware control or validated laptop RPM profile.");
