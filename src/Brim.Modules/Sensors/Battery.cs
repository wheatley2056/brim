using Windows.System.Power;
using WinBattery = Windows.Devices.Power.Battery;

namespace Brim.Modules.Sensors;

/// <summary>Akkuzustand zu einem Zeitpunkt.</summary>
public sealed record BatteryState(
    bool Present,
    int Percent,
    bool Charging,
    bool PluggedIn,
    TimeSpan? Remaining,
    TimeSpan? TimeToFull,
    bool EnergySaver)
{
    public static BatteryState None { get; } = new(false, 0, false, false, null, null, false);
}

public interface IBatterySource : IDisposable
{
    /// <summary>Etwas am Akku hat sich geändert; von einem beliebigen Thread.</summary>
    event Action? Changed;

    BatteryState Read();
}

/// <summary>
/// <c>PowerManager</c> mit Ereignissen statt Abfragen (Plan 4.6); die Ladezeit bis voll rechnet
/// der Bericht von <c>Battery.AggregateBattery</c> aus Ladeleistung und fehlender Kapazität.
/// </summary>
internal sealed class PowerManagerBatterySource : IBatterySource
{
    public PowerManagerBatterySource()
    {
        PowerManager.BatteryStatusChanged += OnChanged;
        PowerManager.RemainingChargePercentChanged += OnChanged;
        PowerManager.PowerSupplyStatusChanged += OnChanged;
        PowerManager.EnergySaverStatusChanged += OnChanged;
        PowerManager.RemainingDischargeTimeChanged += OnChanged;
    }

    public event Action? Changed;

    private void OnChanged(object? sender, object e) => Changed?.Invoke();

    public BatteryState Read()
    {
        var status = PowerManager.BatteryStatus;
        if (status == BatteryStatus.NotPresent)
        {
            return BatteryState.None;
        }
        var charging = status == BatteryStatus.Charging;
        var remaining = PowerManager.RemainingDischargeTime;
        TimeSpan? timeToFull = null;
        if (charging)
        {
            try
            {
                var report = WinBattery.AggregateBattery.GetReport();
                if (report.ChargeRateInMilliwatts is > 0 and var rate
                    && report.FullChargeCapacityInMilliwattHours is { } full
                    && report.RemainingCapacityInMilliwattHours is { } left
                    && full > left)
                {
                    timeToFull = TimeSpan.FromHours((double)(full - left) / rate);
                }
            }
            catch (Exception ex)
            {
                Core.Log.Warn("Akkubericht nicht lesbar", ex);
            }
        }
        return new BatteryState(
            Present: true,
            Percent: Math.Clamp(PowerManager.RemainingChargePercent, 0, 100),
            Charging: charging,
            PluggedIn: PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent,
            Remaining: !charging && remaining > TimeSpan.Zero && remaining < TimeSpan.FromDays(2) ? remaining : null,
            TimeToFull: timeToFull,
            EnergySaver: PowerManager.EnergySaverStatus == EnergySaverStatus.On);
    }

    public void Dispose()
    {
        PowerManager.BatteryStatusChanged -= OnChanged;
        PowerManager.RemainingChargePercentChanged -= OnChanged;
        PowerManager.PowerSupplyStatusChanged -= OnChanged;
        PowerManager.EnergySaverStatusChanged -= OnChanged;
        PowerManager.RemainingDischargeTimeChanged -= OnChanged;
    }
}
