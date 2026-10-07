namespace myFanControl.Editing;

public sealed record FanResponseSettings(int HeatingHysteresis = 0, int CoolingHysteresis = 2,
    int HeatingDelaySeconds = 0, int CoolingDelaySeconds = 0)
{
    public bool IsValid => HeatingHysteresis is >= 0 and <= 10 && CoolingHysteresis is >= 0 and <= 10 &&
        HeatingDelaySeconds is >= 0 and <= 10 && CoolingDelaySeconds is >= 0 and <= 10;
}
