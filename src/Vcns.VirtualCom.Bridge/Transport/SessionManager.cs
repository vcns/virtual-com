using System.Net;
using Vcns.VirtualCom.Bridge.Configuration;
using Vcns.VirtualCom.Bridge.Diagnostics;

namespace Vcns.VirtualCom.Bridge.Transport;

internal sealed class StartRejectedException(string message) : Exception(message);

internal sealed record LastOutcome(SessionOutcome Outcome, string Message, DateTimeOffset At);

/// <summary>
/// Owns the running sessions and enforces single-session access: at most one session per target endpoint
/// (matched by configured name and by resolved address), plus a release interval after each session closes.
/// </summary>
internal sealed class SessionManager : ISessionCoordinator
{
    private readonly object _gate = new();
    private readonly List<ProxySession> _active = [];
    private readonly Dictionary<string, DateTimeOffset> _releasedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, LastOutcome> _lastOutcomes = [];
    private readonly Func<ProxyOptions> _options;
    private readonly BridgeEdition _edition;

    public SessionManager(Func<ProxyOptions> options, BridgeEdition? edition = null)
    {
        _options = options;
        _edition = edition ?? BridgeEdition.Generic;
    }

    /// <summary>Raised on a thread-pool thread whenever a session's state or status text changes.</summary>
    public event Action<ProxySession>? SessionChanged;

    public IReadOnlyList<ProxySession> ActiveSessions
    {
        get
        {
            lock (_gate)
                return _active.ToArray();
        }
    }

    public ProxySession? Find(Guid profileId)
    {
        lock (_gate)
            return _active.FirstOrDefault(s => s.Profile.Id == profileId);
    }

    public LastOutcome? GetLastOutcome(Guid profileId)
    {
        lock (_gate)
            return _lastOutcomes.GetValueOrDefault(profileId);
    }

    /// <exception cref="StartRejectedException">The profile is invalid or would conflict with an active session.</exception>
    public ProxySession Start(ConnectionProfile profile, PortTarget target)
    {
        var errors = ConfigValidator.ValidateProfile(profile);
        if (errors.Count > 0)
            throw new StartRejectedException(string.Join(Environment.NewLine, errors));
        if (!profile.Enabled)
            throw new StartRejectedException($"The profile for {profile.LocalComPort} is disabled.");

        ProxySession session;
        lock (_gate)
        {
            var key = ConfigValidator.EndpointKey(profile.TargetServer, profile.TargetPort);
            foreach (var other in _active)
            {
                if (other.Profile.Id == profile.Id)
                    throw new StartRejectedException($"{profile.LocalComPort} is already {(other.State == SessionState.Connected ? "connected" : "starting")}.");
                if (string.Equals(other.Profile.LocalComPort, profile.LocalComPort, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(other.PortDescription, target.Description, StringComparison.OrdinalIgnoreCase))
                    throw new StartRejectedException($"{profile.LocalComPort} is already in use by another active profile.");
                if (ConfigValidator.EndpointKey(other.Profile.TargetServer, other.Profile.TargetPort) == key)
                {
                    throw new StartRejectedException(
                        $"{profile.Endpoint} is already in use by {other.Profile.LocalComPort}. The {_edition.DeviceNoun} accepts only one session at a time; "
                        + $"disconnect {other.Profile.LocalComPort} first.");
                }
            }

            session = new ProxySession(profile.Clone(), target, _options().Clone(), this, _edition);
            _active.Add(session);
            _lastOutcomes.Remove(profile.Id);
        }

        Log.Info($"Starting {profile.LocalComPort} → {profile.Endpoint} (proxy side {target.Description}).");
        session.Start();
        SessionChanged?.Invoke(session);
        return session;
    }

    public void Stop(Guid profileId) => Find(profileId)?.Stop();

    /// <summary>Stops every session and waits up to <paramref name="timeout"/> for their sockets and ports to close.</summary>
    public async Task StopAllAsync(TimeSpan timeout)
    {
        var sessions = ActiveSessions;
        foreach (var session in sessions)
            session.Stop();
        try
        {
            await Task.WhenAll(sessions.Select(s => s.Completion)).WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            Log.Warn($"{sessions.Count(s => !s.IsEnded)} session(s) did not close within {timeout.TotalSeconds:0} s.");
        }
    }

    bool ISessionCoordinator.TryClaim(ProxySession session, IReadOnlyCollection<IPEndPoint> endpoints, out string? holder)
    {
        lock (_gate)
        {
            var conflict = _active.FirstOrDefault(other => other != session && other.ClaimedEndpoints.Intersect(endpoints).Any());
            holder = conflict?.Profile.LocalComPort;
            if (conflict is not null)
                return false;
            session.ClaimedEndpoints = endpoints;
            return true;
        }
    }

    TimeSpan ISessionCoordinator.GetReleaseDelay(ProxySession session)
    {
        var interval = TimeSpan.FromSeconds(session.Options.ReleaseIntervalSeconds);
        lock (_gate)
        {
            var latest = EndpointKeys(session)
                .Select(k => _releasedAt.TryGetValue(k, out var at) ? at : DateTimeOffset.MinValue)
                .Max();
            if (latest == DateTimeOffset.MinValue)
                return TimeSpan.Zero;
            var remaining = latest + interval - DateTimeOffset.Now;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    void ISessionCoordinator.Changed(ProxySession session) => SessionChanged?.Invoke(session);

    void ISessionCoordinator.Ended(ProxySession session)
    {
        lock (_gate)
        {
            _active.Remove(session);
            if (session.TcpOpened)
            {
                var now = DateTimeOffset.Now;
                foreach (var key in EndpointKeys(session))
                    _releasedAt[key] = now;
            }
            _lastOutcomes[session.Profile.Id] = new LastOutcome(session.Outcome, session.OutcomeMessage ?? "", session.EndedAt ?? DateTimeOffset.Now);
        }
        SessionChanged?.Invoke(session);
    }

    private static IEnumerable<string> EndpointKeys(ProxySession session) =>
        session.ClaimedEndpoints.Select(e => e.ToString())
            .Append(ConfigValidator.EndpointKey(session.Profile.TargetServer, session.Profile.TargetPort));
}
