using Roshiq.Data;

namespace Roshiq.Recommendations;

/// <summary>Ближайшее событие матча: когда наступит (игровые часы) и через сколько секунд.</summary>
public sealed record UpcomingEvent(TimerRule Rule, int AtSec, int InSec);

/// <summary>
/// Расписание периодических событий по игровым часам (clock_time GSI). Используются только
/// общеизвестные правила игры из timers.txt — никакой информации о противнике.
/// </summary>
public static class GameTimers
{
    /// <summary>Следующее появление строго после clock (или первое, если оно ещё не наступило).</summary>
    public static int? NextOccurrence(TimerRule rule, int clock)
    {
        int at;
        if (clock < rule.FirstSec) at = rule.FirstSec;
        else if (rule.IntervalSec <= 0) return null;
        else
        {
            long n = ((long)clock - rule.FirstSec) / rule.IntervalSec + 1;
            long next = rule.FirstSec + n * rule.IntervalSec;
            if (next > int.MaxValue) return null;
            at = (int)next;
        }
        if (rule.LastSec is int last && at > last) return null;
        return at;
    }

    public static IReadOnlyList<UpcomingEvent> Upcoming(IEnumerable<TimerRule> rules, int clock, Func<string, bool> enabled, int max = 4)
    {
        var list = new List<UpcomingEvent>();
        foreach (var rule in rules)
        {
            if (!enabled(rule.Key)) continue;
            if (NextOccurrence(rule, clock) is int at) list.Add(new UpcomingEvent(rule, at, at - clock));
        }
        return list.OrderBy(e => e.InSec).ThenBy(e => e.Rule.Key, StringComparer.Ordinal).Take(max).ToList();
    }

    /// <summary>Формат «1:05» / «-0:30».</summary>
    public static string Clock(int seconds)
    {
        var abs = Math.Abs((long)seconds);
        return $"{(seconds < 0 ? "-" : "")}{abs / 60}:{abs % 60:00}";
    }
}

/// <summary>
/// Однократные предупреждения «скоро событие»: каждое появление объявляется не больше одного раза,
/// когда до него осталось не больше LeadSec. Перемотка часов назад (новый матч) сбрасывает состояние.
/// </summary>
public sealed class TimerAnnouncer
{
    private readonly HashSet<(string Key, int At)> _announced = new();
    private int? _lastClock;

    public void Reset()
    {
        _announced.Clear();
        _lastClock = null;
    }

    /// <summary>События, о которых нужно предупредить прямо сейчас.</summary>
    public IReadOnlyList<UpcomingEvent> Due(IEnumerable<TimerRule> rules, int clock, Func<string, bool> enabled)
    {
        if (_lastClock is int prev && clock + 30 < prev) _announced.Clear();
        // пропуск вперёд (переподключение) — не объявлять то, что уже прошло
        _lastClock = clock;
        var due = new List<UpcomingEvent>();
        foreach (var e in GameTimers.Upcoming(rules, clock, enabled, int.MaxValue))
        {
            if (e.InSec <= 0 || e.InSec > e.Rule.LeadSec) continue;
            if (_announced.Add((e.Rule.Key, e.AtSec))) due.Add(e);
        }
        if (_announced.Count > 200) _announced.RemoveWhere(x => x.At < clock - 60);
        return due;
    }
}

public enum RoshanPhase { None, Dead, MaybeAlive }

/// <summary>Состояние после отмеченного убийства Рошана (по игровым часам).</summary>
public sealed record RoshanState(RoshanPhase Phase, int KilledAt, int AegisLeftSec, int RespawnMinAt, int RespawnMaxAt);

/// <summary>
/// Таймер Рошана по ручной отметке игрока (горячая клавиша или кнопка), как секундомер.
/// Патч 7.42: базовое возрождение — 7–10 минут после первого убийства и на минуту меньше за каждое следующее,
/// но не меньше 5 минут (5–8). Аегис действует 5 минут.
/// </summary>
public sealed class RoshanTracker
{
    public const int AegisSec = 300;
    /// <summary>Базовое окно до сокращения за убийства.</summary>
    public const int RespawnMinSec = 480;
    public const int RespawnMaxSec = 660;
    /// <summary>7.42: каждое убийство сокращает базовое время на минуту, до 5 минут.</summary>
    public const int RespawnFloorSec = 300;
    public const int RespawnStepSec = 60;

    private readonly HashSet<string> _announced = new();

    public int? KilledAt { get; private set; }
    /// <summary>Сколько раз Рошана отметили убитым в этом матче.</summary>
    public int Kills { get; private set; }

    public static int RespawnMinFor(int kill) => Math.Max(RespawnFloorSec, RespawnMinSec - RespawnStepSec * Math.Clamp(kill, 0, 3));
    public static int RespawnMaxFor(int kill) => RespawnMinFor(kill) + (RespawnMaxSec - RespawnMinSec);
    public int CurrentRespawnMin => RespawnMinFor(Math.Max(1, Kills));
    public int CurrentRespawnMax => RespawnMaxFor(Math.Max(1, Kills));

    public void MarkKilled(int clock)
    {
        Kills++;
        KilledAt = clock;
        _announced.Clear();
    }

    /// <summary>Снять ошибочную отметку: убийство не засчитывается.</summary>
    public void Undo()
    {
        if (KilledAt != null) Kills = Math.Max(0, Kills - 1);
        KilledAt = null;
        _announced.Clear();
    }

    /// <summary>Убрать таймер (Рошан уже возродился). Счёт убийств за матч сохраняется.</summary>
    public void Clear()
    {
        KilledAt = null;
        _announced.Clear();
    }

    /// <summary>Новый матч: всё с нуля.</summary>
    public void Reset()
    {
        Kills = 0;
        Clear();
    }

    public RoshanState State(int clock)
    {
        if (KilledAt is not int k) return new RoshanState(RoshanPhase.None, 0, 0, 0, 0);
        int since = clock - k;
        if (since < -30)
        {
            // часы ушли назад — это новый матч
            Reset();
            return new RoshanState(RoshanPhase.None, 0, 0, 0, 0);
        }
        int min = CurrentRespawnMin, max = CurrentRespawnMax;
        if (since > max + 120)
        {
            // прошло слишком много — отметка больше не актуальна (счёт убийств остаётся)
            Clear();
            return new RoshanState(RoshanPhase.None, 0, 0, 0, 0);
        }
        int aegis = Math.Max(0, AegisSec - Math.Max(0, since));
        var phase = since < min ? RoshanPhase.Dead : RoshanPhase.MaybeAlive;
        return new RoshanState(phase, k, aegis, k + min, k + max);
    }

    /// <summary>Ключи предупреждений, которые нужно показать сейчас: aegis_30, respawn_min, respawn_max.</summary>
    public IReadOnlyList<string> Due(int clock)
    {
        var st = State(clock);
        var list = new List<string>();
        if (st.Phase == RoshanPhase.None) return list;
        int since = clock - st.KilledAt;
        void Check(string key, bool cond) { if (cond && _announced.Add(key)) list.Add(key); }
        Check("rosh_aegis_soon", since >= AegisSec - 30 && since < AegisSec);
        int min = CurrentRespawnMin, max = CurrentRespawnMax;
        Check("rosh_respawn_min", since >= min && since < min + 30);
        Check("rosh_respawn_max", since >= max && since < max + 30);
        return list;
    }
}

/// <summary>Оценка восстановления зарядов глифа по ручным отметкам.</summary>
public sealed class GlyphTracker
{
    public const int CooldownSec = 360;
    public const int ReuseSec = 7;
    private int _missing;
    private int _nextChargeAt;
    private bool _announced;
    private (int? UsedAt, int Missing, int NextChargeAt, bool Announced) _beforeMark;

    public int? UsedAt { get; private set; }

    public void MarkUsed(int clock)
    {
        Update(clock, 0);
        _beforeMark = (UsedAt, _missing, _nextChargeAt, _announced);
        UsedAt = clock;
        if (_missing == 0)
        {
            _nextChargeAt = clock + CooldownSec;
            _announced = false;
        }
        _missing = Math.Min(2, _missing + 1);
    }

    public void Undo() => (UsedAt, _missing, _nextChargeAt, _announced) = _beforeMark;

    public void Clear()
    {
        UsedAt = null;
        _missing = 0;
        _nextChargeAt = 0;
        _announced = false;
        _beforeMark = default;
    }

    public int? ReadyIn(int clock)
    {
        Update(clock);
        return UsedAt.HasValue ? Math.Max(0, _nextChargeAt - clock) : null;
    }

    public bool DueReady(int clock)
    {
        Update(clock);
        if (UsedAt == null || clock < _nextChargeAt || _announced) return false;
        _announced = true;
        return true;
    }

    private void Update(int clock, int alertWindow = 30)
    {
        if (UsedAt is not int used) return;
        if (clock < used - 30)
        {
            Clear();
            return;
        }

        // Оставляем короткое окно для уведомления о восстановлении заряда.
        while (_missing > 0 && clock >= _nextChargeAt + alertWindow)
        {
            _missing--;
            if (_missing == 0) break;
            _nextChargeAt += CooldownSec;
            _announced = false;
        }
        if (_missing == 0 && clock > _nextChargeAt + 60) Clear();
    }
}
