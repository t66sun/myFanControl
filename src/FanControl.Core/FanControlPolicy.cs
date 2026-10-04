namespace FanControl.Core;

public readonly record struct RpmLimits(int Minimum, int Maximum);
public readonly record struct CurvePoint(double Celsius, int Rpm);

/// <summary>Pure calculation. Limits must come from a validated hardware profile.</summary>
public sealed class FanCurve
{
    private readonly CurvePoint[] points;
    public RpmLimits Limits { get; }
    public IReadOnlyList<CurvePoint> Points { get; }
    public FanCurve(IEnumerable<CurvePoint> points, RpmLimits limits)
    {
        if (limits.Minimum < 0 || limits.Maximum <= limits.Minimum) throw new ArgumentException("Invalid RPM limits");
        this.points = points.ToArray();
        if (this.points.Length < 2) throw new ArgumentException("At least two curve points are required");
        for (int i=0;i<this.points.Length;i++)
        {
            var p=this.points[i];
            if (!ValidTemperature(p.Celsius) || p.Rpm<limits.Minimum || p.Rpm>limits.Maximum) throw new ArgumentException("Invalid curve point");
            if (i>0 && (p.Celsius<=this.points[i-1].Celsius || p.Rpm<this.points[i-1].Rpm)) throw new ArgumentException("Curve must increase in temperature and not decrease in RPM");
        }
        if (this.points[^1].Rpm!=limits.Maximum) throw new ArgumentException("The hottest point must request the profile maximum");
        Limits=limits;Points=Array.AsReadOnly(this.points);
    }
    internal static bool ValidTemperature(double value) => double.IsFinite(value) && value is >= -100 and <= 150;
    public int Evaluate(double celsius)
    {
        if (!ValidTemperature(celsius)) throw new ArgumentOutOfRangeException(nameof(celsius));
        if(celsius<=points[0].Celsius)return points[0].Rpm;
        for(int i=1;i<points.Length;i++)
            if(celsius<=points[i].Celsius)
            {
                var a=points[i-1];var b=points[i];
                double fraction=(celsius-a.Celsius)/(b.Celsius-a.Celsius);
                return (int)Math.Round(a.Rpm+fraction*(b.Rpm-a.Rpm),MidpointRounding.AwayFromZero);
            }
        return points[^1].Rpm;
    }
}

public enum ControlAction { Hold, RequestRpm, RestoreFirmwareAutomatic }
public enum DecisionReason { InitialSample, Heating, Cooling, AtTarget, CoolingHysteresis, CoolingRateLimit, WriteInterval, HighTemperature, SensorMissing, SensorInvalid, StaleSample, FutureSample, InvalidHistory }
public sealed record TemperatureFrame(DateTimeOffset CapturedAtUtc, IReadOnlyDictionary<string,double?> Values);
/// <summary>Last successfully applied command, not the last proposed command.</summary>
public sealed record AppliedControl(int Rpm, double TemperatureCelsius, DateTimeOffset AppliedAtUtc);
public sealed record ControlDecision(ControlAction Action, int? RequestedRpm, double? TemperatureCelsius, DecisionReason Reason);

/// <summary>No driver, device, settings writes, or mutable control state.</summary>
public sealed class FanControlPolicy
{
    private readonly FanCurve curve;
    private readonly string[] requiredSensors;
    private readonly TimeSpan maximumAge, minimumInterval;
    private readonly double coolingHysteresis, overheat;
    private readonly int coolingRpmPerSecond;
    public FanControlPolicy(FanCurve curve,IEnumerable<string> requiredSensors,TimeSpan maximumAge,TimeSpan minimumInterval,
        double coolingHysteresis,double overheat,int coolingRpmPerSecond)
    {
        this.requiredSensors=requiredSensors.ToArray();
        if(this.requiredSensors.Length==0 || this.requiredSensors.Any(string.IsNullOrWhiteSpace) || this.requiredSensors.Distinct(StringComparer.Ordinal).Count()!=this.requiredSensors.Length)
            throw new ArgumentException("Required sensors must be nonempty and unique");
        if(maximumAge<=TimeSpan.Zero || minimumInterval<=TimeSpan.Zero || coolingRpmPerSecond<=0 || !double.IsFinite(coolingHysteresis) || coolingHysteresis<0 || coolingHysteresis>30)
            throw new ArgumentException("Invalid timing or cooling parameters");
        if(!FanCurve.ValidTemperature(overheat) || overheat<curve.Points[^1].Celsius)throw new ArgumentException("Invalid high-temperature threshold");
        this.curve=curve;this.maximumAge=maximumAge;this.minimumInterval=minimumInterval;
        this.coolingHysteresis=coolingHysteresis;this.overheat=overheat;this.coolingRpmPerSecond=coolingRpmPerSecond;
    }
    public ControlDecision Evaluate(TemperatureFrame frame,DateTimeOffset now,AppliedControl? applied)
    {
        static ControlDecision Restore(DecisionReason reason)=>new(ControlAction.RestoreFirmwareAutomatic,null,null,reason);
        if(frame.CapturedAtUtc>now)return Restore(DecisionReason.FutureSample);
        if(now-frame.CapturedAtUtc>maximumAge)return Restore(DecisionReason.StaleSample);
        double temperature=double.NegativeInfinity;
        foreach(string sensor in requiredSensors)
        {
            if(!frame.Values.TryGetValue(sensor,out var value) || value is null)return Restore(DecisionReason.SensorMissing);
            if(!FanCurve.ValidTemperature(value.Value))return Restore(DecisionReason.SensorInvalid);
            temperature=Math.Max(temperature,value.Value);
        }
        if(applied is not null && (applied.AppliedAtUtc>now || !FanCurve.ValidTemperature(applied.TemperatureCelsius) || applied.Rpm<curve.Limits.Minimum || applied.Rpm>curve.Limits.Maximum))
            return Restore(DecisionReason.InvalidHistory);
        int target=temperature>=overheat?curve.Limits.Maximum:curve.Evaluate(temperature);
        bool high=temperature>=overheat;
        if(applied is null)return new(ControlAction.RequestRpm,target,temperature,high?DecisionReason.HighTemperature:DecisionReason.InitialSample);
        if(target==applied.Rpm)return new(ControlAction.Hold,null,temperature,high?DecisionReason.HighTemperature:DecisionReason.AtTarget);
        if(!high && now-applied.AppliedAtUtc<minimumInterval)return new(ControlAction.Hold,null,temperature,DecisionReason.WriteInterval);
        if(target<applied.Rpm)
        {
            if(temperature>applied.TemperatureCelsius-coolingHysteresis)return new(ControlAction.Hold,null,temperature,DecisionReason.CoolingHysteresis);
            double allowableDrop=Math.Floor((now-applied.AppliedAtUtc).TotalSeconds*coolingRpmPerSecond);
            target=(int)Math.Max(target,applied.Rpm-allowableDrop);
            if(target==applied.Rpm)return new(ControlAction.Hold,null,temperature,DecisionReason.CoolingRateLimit);
        }
        return new(ControlAction.RequestRpm,target,temperature,high?DecisionReason.HighTemperature:target>applied.Rpm?DecisionReason.Heating:DecisionReason.Cooling);
    }
}
