namespace EasyLive.Core;

public sealed record StreamStats(int DroppedFramesDelta, int SendQueueMs, int EncodeLagMs, TimeSpan Elapsed);

public abstract record AdaptDecision;

public sealed record Hold : AdaptDecision;

public sealed record SetBitrate(int Kbps) : AdaptDecision;

/// <summary>Step to a lower rung with a new keyframe.</summary>
public sealed record Reinit(string Reason) : AdaptDecision;

/// <summary>Live bitrate logic. Decide() is called once per second; each call counts as one second.</summary>
public sealed class AdaptationController
{
    private const int QueueCongestedMs = 1500;
    private const int EncodeLagCongestedMs = 100;
    private const int HealthySecondsToRaise = 20;
    private const int FloorCongestedSecondsToReinit = 10;

    private readonly QualityRung _rung;
    private int _currentKbps;
    private int _healthyStreak;
    private int _floorCongestedStreak;

    public AdaptationController(QualityRung rung, int startKbps)
    {
        _rung = rung;
        _currentKbps = startKbps;
    }

    public AdaptDecision Decide(StreamStats s)
    {
        bool congested = s.SendQueueMs > QueueCongestedMs
            || s.DroppedFramesDelta > 0
            || s.EncodeLagMs > EncodeLagCongestedMs;

        if (congested)
        {
            _healthyStreak = 0;
            if (_currentKbps <= _rung.MinKbps)
            {
                if (++_floorCongestedStreak >= FloorCongestedSecondsToReinit)
                {
                    _floorCongestedStreak = 0;
                    return new Reinit($"Congested at minimum bitrate for {FloorCongestedSecondsToReinit}s");
                }
                return new Hold();
            }

            _floorCongestedStreak = 0;
            _currentKbps = Math.Max(_rung.MinKbps, _currentKbps * 75 / 100);
            return new SetBitrate(_currentKbps);
        }

        _floorCongestedStreak = 0;
        if (++_healthyStreak >= HealthySecondsToRaise && _currentKbps < _rung.MaxKbps)
        {
            _healthyStreak = 0;
            _currentKbps = Math.Min(_rung.MaxKbps, _currentKbps * 110 / 100);
            return new SetBitrate(_currentKbps);
        }
        return new Hold();
    }
}
