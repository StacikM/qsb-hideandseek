using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OWML.Common;
using QSB.Player;
using UnityEngine;
using UnityEngine.InputSystem;
using Random = UnityEngine.Random;

namespace QSBHideAndSeek
{
    public enum GamePhase
    {
        Idle,
        Voting,
        Countdown,
        Seeking,
    }

    public enum PlayerRole
    {
        None,
        Hider,
        Seeker,
    }

    public struct ResultLine
    {
        public string Name;
        public bool WasHider;
        public float Seconds;
    }

    public partial class HideAndSeekGame : MonoBehaviour
    {
        public static HideAndSeekGame Instance { get; private set; }

        private const string MessageKey = "Stacik.QSBHideAndSeek";

        // local (per player) settings.
        public RoundSettings LocalSettings = new RoundSettings();
        public bool DisableTimeLoop = true;
        public Key MenuKey = Key.F8;

        // host's settings (sent by vote)
        public RoundSettings Round = new RoundSettings();

        public GamePhase Phase { get; private set; } = GamePhase.Idle;
        public int PlanetIndex { get; private set; } = -1;

        private IQSBAPI _api;
        private IModConsole _console;
        private bool _inSolarSystem;

        private readonly Dictionary<uint, PlayerRole> _roles = new Dictionary<uint, PlayerRole>();
        private readonly Dictionary<uint, bool> _startedAsHider = new Dictionary<uint, bool>();
        private readonly Dictionary<uint, float> _hideTimes = new Dictionary<uint, float>();
        private readonly Dictionary<uint, string> _names = new Dictionary<uint, string>();
        private readonly Dictionary<uint, int> _votes = new Dictionary<uint, int>();
        private readonly HashSet<uint> _participants = new HashSet<uint>();
        private readonly HashSet<uint> _outPlayers = new HashSet<uint>();
        private readonly Dictionary<uint, float> _lastTagSent = new Dictionary<uint, float>();

        private float _phaseStart;
        private float _goTime;
        private bool _endSent;

        // Local "dead / found" state.
        private bool _isOut;
        private float _outUntil;
        private string _outText = "";

        // Results screen.
        private List<ResultLine> _results;
        private float _resultsUntil;
        private float _roundSeconds;

        // Small on-screen message.
        private string _toast = "";
        private float _toastUntil;

        // Play-area barrier: a sphere around the chosen planet that the player and ship can't leave.
        private OWRigidbody _barrierBody;
        private float _barrierRadius;
        private float _barrierWarnUntil;
        private float _nextBarrierReturn;

        // The soft barrier keeps us within ~1m of the limit. Being further out than this means something
        // is carrying us (a Nomai shuttle, a warp pad, a black hole...) and a wall can't fix it.
        private const float HardEscapeMeters = 25f;

        // True while we have the player's eyes shut ourselves (so we know to open them again).
        private bool _eyesForcedShut;

        // -------------------------------------------------------------------------------------
        // Setup
        // -------------------------------------------------------------------------------------

        public void Init(IQSBAPI api, IModBehaviour mod, IModConsole console)
        {
            Instance = this;
            _api = api;
            _console = console;

            // No RegisterRequiredForAllPlayers here: QSB already registers every mod that lists it
            // as a dependency in its manifest, and registering twice throws.
            api.RegisterHandler<string>(MessageKey, OnMessage);
            api.OnPlayerLeave().AddListener(OnPlayerLeave);
        }

        public void OnSceneLoaded(OWScene scene)
        {
            _inSolarSystem = scene == OWScene.SolarSystem;
            ResetRound();
            _results = null;
        }

        private uint LocalId => _api.GetLocalPlayerID();

        private uint HostId
        {
            get
            {
                var ids = _api.GetPlayerIDs();
                return ids.Length > 0 ? ids[0] : uint.MaxValue;
            }
        }

        private bool IsHost => _api.GetIsHost();

        public PlayerRole LocalRole => GetRole(LocalId);

        public PlayerRole GetRole(uint id)
            => _roles.TryGetValue(id, out var role) ? role : PlayerRole.None;

        private bool RoundIsLive => Phase == GamePhase.Countdown || Phase == GamePhase.Seeking;

        private void Log(string text, MessageType type = MessageType.Info)
            => _console?.WriteLine(text, type);

        private void Toast(string text, float seconds = 4f)
        {
            _toast = text;
            _toastUntil = Time.unscaledTime + seconds;
        }

        private void Send(string payload, uint to = uint.MaxValue, bool receiveLocally = true)
            => _api.SendMessage(MessageKey, payload, to, receiveLocally);

        private string NameOf(uint id)
        {
            if (_names.TryGetValue(id, out var name))
            {
                return name;
            }

            try
            {
                return _api.GetPlayerName(id) ?? ("Player " + id);
            }
            catch
            {
                return "Player " + id;
            }
        }

        private static string Csv(IEnumerable<uint> ids) => string.Join(",", ids.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray());

        private static uint[] ParseCsv(string text)
            => string.IsNullOrEmpty(text)
                ? new uint[0]
                : text.Split(',').Select(x => uint.Parse(x, CultureInfo.InvariantCulture)).ToArray();

        // -------------------------------------------------------------------------------------
        // Per-frame
        // -------------------------------------------------------------------------------------

        private void Update()
        {
            if (_api == null || !_inSolarSystem)
            {
                return;
            }

            FreezeTimeLoop();
            HandleKeys();

            if (IsHost)
            {
                HostTick();
            }

            LocalTick();
        }

        // sun shouldn't do a mi bombo during hide & seek
        private void FreezeTimeLoop()
        {
            if (!DisableTimeLoop)
            {
                return;
            }

            TimeLoop._timeOffset = -Time.timeSinceLevelLoad;
        }

        private void HandleKeys()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return;
            }

            if (keyboard[MenuKey].wasPressedThisFrame)
            {
                OnMenuKey();
            }

            if (Phase == GamePhase.Voting && _participants.Contains(LocalId))
            {
                for (var i = 0; i < Planets.Options.Length && i < 9; i++)
                {
                    if (keyboard[(Key)((int)Key.Digit1 + i)].wasPressedThisFrame)
                    {
                        Send("VOTE|" + i);
                    }
                }
            }

            if (_results != null && keyboard[MenuKey].wasPressedThisFrame)
            {
                _results = null;
            }
        }

        private void OnMenuKey()
        {
            if (!IsHost)
            {
                if (Phase == GamePhase.Idle)
                {
                    Toast("Only the host can start Hide & Seek.");
                }

                return;
            }

            if (Phase == GamePhase.Idle)
            {
                HostStartVote();
            }
            else
            {
                Send("CANCEL");
            }
        }

        // -------------------------------------------------------------------------------------
        // Host logic
        // -------------------------------------------------------------------------------------

        private IEnumerable<uint> EligiblePlayers()
            => _api.GetPlayerIDs().Where(id =>
            {
                try
                {
                    return _api.GetPlayerReady(id) && !_api.GetPlayerDead(id);
                }
                catch
                {
                    return false;
                }
            });

        private void HostStartVote()
        {
            if (!_api.GetIsInMultiplayer())
            {
                Toast("Hide & Seek needs a multiplayer game.");
                return;
            }

            var players = EligiblePlayers().ToArray();
            if (players.Length < 2)
            {
                Toast("Need at least 2 ready players to play Hide & Seek.");
                return;
            }

            Send("VOTE_START|" + LocalSettings.Serialize() + "|" + Csv(players));
        }

        private void HostTick()
        {
            var now = Time.unscaledTime;

            switch (Phase)
            {
                case GamePhase.Voting:
                {
                    var everyoneVoted = _participants.All(_votes.ContainsKey);
                    if (everyoneVoted || now - _phaseStart >= Round.VoteSeconds)
                    {
                        HostFinishVote();
                    }

                    break;
                }
                case GamePhase.Countdown:
                    if (now - _phaseStart >= Round.CountdownSeconds)
                    {
                        Send("GO");
                    }

                    break;
                case GamePhase.Seeking:
                    HostCheckRoundOver();
                    break;
            }
        }

        private void HostFinishVote()
        {
            // Only players that are still around can play
            var present = _api.GetPlayerIDs();
            var players = _participants.Where(id => present.Contains(id)).ToList();
            if (players.Count < 2)
            {
                Send("CANCEL");
                return;
            }

            // russian roulette
            var tally = new int[Planets.Options.Length];
            foreach (var vote in _votes)
            {
                if (players.Contains(vote.Key) && vote.Value >= 0 && vote.Value < tally.Length)
                {
                    tally[vote.Value]++;
                }
            }

            var best = tally.Max();
            var candidates = Enumerable.Range(0, tally.Length).Where(i => best == 0 || tally[i] == best).ToList();
            var planet = candidates[Random.Range(0, candidates.Count)];

            var seekerCount = Mathf.Clamp(Round.SeekerCount, 1, players.Count - 1);
            var seekers = players.OrderBy(_ => Random.value).Take(seekerCount).ToList();
            var hiders = players.Except(seekers).ToList();

            Send("START|" + planet + "|" + Csv(seekers) + "|" + Csv(hiders));
        }

        private void HostCheckRoundOver()
        {
            if (_endSent || Phase != GamePhase.Seeking)
            {
                return;
            }

            var present = _api.GetPlayerIDs();

            // Players that left count as gone
            foreach (var id in _roles.Keys.ToList())
            {
                if (!present.Contains(id))
                {
                    _roles.Remove(id);
                    if (_startedAsHider.TryGetValue(id, out var wasHider) && wasHider && !_hideTimes.ContainsKey(id))
                    {
                        _hideTimes[id] = Time.unscaledTime - _goTime;
                    }
                }
            }

            var hiders = _roles.Where(x => x.Value == PlayerRole.Hider).Select(x => x.Key).ToList();
            var seekers = _roles.Where(x => x.Value == PlayerRole.Seeker).Select(x => x.Key).ToList();

            if (hiders.Count == 0)
            {
                _endSent = true;
                Send("END|" + BuildResultsPayload());
                return;
            }

            if (seekers.Count == 0)
            {
                // every seeker ragequit
                Send("PROMOTE|" + hiders[Random.Range(0, hiders.Count)]);
            }
        }

        private string BuildResultsPayload()
        {
            var c = CultureInfo.InvariantCulture;
            var lines = new List<string>();
            foreach (var id in _startedAsHider.Keys)
            {
                var hider = _startedAsHider[id];
                var seconds = hider && _hideTimes.TryGetValue(id, out var t) ? t : 0f;
                lines.Add(id.ToString(c) + ":" + seconds.ToString("0.0", c) + ":" + (hider ? "H" : "S"));
            }

            var total = Time.unscaledTime - _goTime;
            return total.ToString("0.0", c) + "|" + string.Join(";", lines.ToArray());
        }

        // -------------------------------------------------------------------------------------
        // Messages (run on every client, including the host himself)
        // -------------------------------------------------------------------------------------

        private void OnMessage(uint from, string payload)
        {
            try
            {
                Handle(from, payload);
            }
            catch (Exception e)
            {
                Log($"Error handling message '{payload}' from {from}: {e}", MessageType.Error);
            }
        }

        private void Handle(uint from, string payload)
        {
            var parts = payload.Split('|');
            var fromHost = from == HostId;

            switch (parts[0])
            {
                case "VOTE_START":
                    if (!fromHost || Phase != GamePhase.Idle)
                    {
                        return;
                    }

                    ResetRound();
                    Round = RoundSettings.Deserialize(parts[1]);
                    foreach (var id in ParseCsv(parts[2]))
                    {
                        _participants.Add(id);
                        _names[id] = SafeName(id);
                    }

                    Phase = GamePhase.Voting;
                    _phaseStart = Time.unscaledTime;
                    _results = null;
                    break;

                case "VOTE":
                    if (Phase == GamePhase.Voting && _participants.Contains(from))
                    {
                        _votes[from] = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    }

                    break;

                case "START":
                    if (!fromHost || Phase != GamePhase.Voting)
                    {
                        return;
                    }

                    BeginRound(
                        int.Parse(parts[1], CultureInfo.InvariantCulture),
                        ParseCsv(parts[2]),
                        ParseCsv(parts[3]));
                    break;

                case "GO":
                    if (!fromHost || Phase != GamePhase.Countdown)
                    {
                        return;
                    }

                    Phase = GamePhase.Seeking;
                    _goTime = Time.unscaledTime;
                    if (LocalRole == PlayerRole.Seeker && !_isOut)
                    {
                        UnlockLocalPlayer();
                        SetBlind(false);
                    }

                    break;

                case "CAUGHT":
                {
                    var victim = uint.Parse(parts[1], CultureInfo.InvariantCulture);
                    var seconds = float.Parse(parts[2], CultureInfo.InvariantCulture);
                    OnCaught(victim, seconds);
                    break;
                }

                case "PROMOTE":
                {
                    if (!fromHost)
                    {
                        return;
                    }

                    var id = uint.Parse(parts[1], CultureInfo.InvariantCulture);
                    if (GetRole(id) == PlayerRole.Hider)
                    {
                        _roles[id] = PlayerRole.Seeker;
                        _hideTimes[id] = Time.unscaledTime - _goTime;
                        if (id == LocalId)
                        {
                            Toast("You are now a SEEKER!", 5f);
                        }
                    }

                    break;
                }

                case "OUT":
                    _outPlayers.Add(from);
                    SetRemoteVisible(from, false);
                    break;

                case "BACK":
                    _outPlayers.Remove(from);
                    SetRemoteVisible(from, true);
                    break;

                case "END":
                    if (!fromHost || !RoundIsLive)
                    {
                        return;
                    }

                    EndRound(parts[1], parts[2]);
                    break;

                case "CANCEL":
                    if (!fromHost || Phase == GamePhase.Idle)
                    {
                        return;
                    }

                    var wasLive = RoundIsLive;
                    ResetRound();
                    Toast(wasLive ? "Hide & Seek was cancelled by the host." : "Hide & Seek vote cancelled.");
                    break;
            }
        }

        private string SafeName(uint id)
        {
            try
            {
                return _api.GetPlayerName(id) ?? ("Player " + id);
            }
            catch
            {
                return "Player " + id;
            }
        }

        private void BeginRound(int planet, uint[] seekers, uint[] hiders)
        {
            _roles.Clear();
            _startedAsHider.Clear();
            _hideTimes.Clear();
            _outPlayers.Clear();
            _lastTagSent.Clear();
            _endSent = false;

            foreach (var id in seekers)
            {
                _roles[id] = PlayerRole.Seeker;
                _startedAsHider[id] = false;
                _names[id] = SafeName(id);
            }

            foreach (var id in hiders)
            {
                _roles[id] = PlayerRole.Hider;
                _startedAsHider[id] = true;
                _names[id] = SafeName(id);
            }

            PlanetIndex = planet;
            Phase = GamePhase.Countdown;
            _phaseStart = Time.unscaledTime;
            _goTime = float.MaxValue;
            _results = null;

            if (!_roles.ContainsKey(LocalId))
            {
                // watch and learn
                Toast("A Hide & Seek round is running. You'll be able to join the next one.", 6f);
                return;
            }

            // Make sure nobody is stuck in a leftover out state.
            if (_isOut)
            {
                _isOut = false;
                Locator.GetPlayerController().UnlockMovement();
            }

            Planets.TeleportLocalPlayer(planet);
            ResetPlayerComponents();
            EnsureSuit();

            if (!Planets.GetBarrier(planet, out _barrierBody, out _barrierRadius))
            {
                _barrierBody = null;
            }

            if (LocalRole == PlayerRole.Seeker)
            {
                LockLocalPlayer();
                SetBlind(true);
            }
        }

        private void OnCaught(uint victim, float seconds)
        {
            if (Phase != GamePhase.Seeking || GetRole(victim) != PlayerRole.Hider)
            {
                return;
            }

            _roles[victim] = PlayerRole.Seeker;
            _hideTimes[victim] = seconds;

            var name = NameOf(victim);
            Toast(victim == LocalId ? "You were found!" : name + " was found!");

            if (victim == LocalId)
            {
                BeginOut("You were found!\nYou are now a SEEKER.");
            }
        }

        private void EndRound(string totalSecondsText, string linesText)
        {
            var c = CultureInfo.InvariantCulture;
            _roundSeconds = float.Parse(totalSecondsText, c);

            var results = new List<ResultLine>();
            foreach (var line in linesText.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = line.Split(':');
                var id = uint.Parse(p[0], c);
                results.Add(new ResultLine
                {
                    Name = NameOf(id),
                    Seconds = float.Parse(p[1], c),
                    WasHider = p[2] == "H",
                });
            }

            // Longest hiding time first, original seekers at the bottom
            _results = results.OrderByDescending(x => x.WasHider).ThenByDescending(x => x.Seconds).ToList();
            _resultsUntil = Time.unscaledTime + 30f;

            if (IsHost)
            {
                PostResultsToChat();
            }

            ResetRound();
        }

        private void PostResultsToChat()
        {
            try
            {
                _api.SendChatMessage("Hide & Seek is over! Time spent hiding:", true, Color.yellow);
                var rank = 1;
                foreach (var line in _results.Where(x => x.WasHider))
                {
                    _api.SendChatMessage($"{rank}. {line.Name} - {FormatTime(line.Seconds)}", true, Color.white);
                    rank++;
                }
            }
            catch (Exception e)
            {
                Log("Couldn't post results to chat: " + e.Message, MessageType.Warning);
            }
        }

        public static string FormatTime(float seconds)
        {
            var total = Mathf.Max(0, Mathf.FloorToInt(seconds));
            return $"{total / 60}:{total % 60:00}";
        }

        private void OnPlayerLeave(uint id)
        {
            _votes.Remove(id);
            _outPlayers.Remove(id);
            _participants.Remove(id);
        }

        // -------------------------------------------------------------------------------------
        // Reset / local player control
        // -------------------------------------------------------------------------------------

        // forget everything
        private void ResetRound()
        {
            var wasLive = RoundIsLive;

            Phase = GamePhase.Idle;
            PlanetIndex = -1;
            _roles.Clear();
            _startedAsHider.Clear();
            _hideTimes.Clear();
            _votes.Clear();
            _participants.Clear();
            _lastTagSent.Clear();
            _endSent = false;
            _barrierBody = null;

            var outBefore = _outPlayers.ToList();
            _outPlayers.Clear();
            foreach (var id in outBefore)
            {
                SetRemoteVisible(id, true);
            }

            if (_isOut)
            {
                _isOut = false;
                if (_inSolarSystem)
                {
                    FinishLocalRespawn(teleport: false);
                }
            }
            else if (wasLive && _inSolarSystem)
            {
                UnlockLocalPlayer();
                SetBlind(false);
            }
        }

        /// <summary>True while the local player must not move at all (blind seeker countdown, or "out").</summary>
        public bool IsLocalFrozen
            => _isOut || (Phase == GamePhase.Countdown && _roles.ContainsKey(LocalId) && LocalRole == PlayerRole.Seeker);

        /// <summary>Shuts / opens the player's eyes using the game's own eyelid effect (the same one sleeping uses).</summary>
        private void SetBlind(bool blind)
        {
            var camera = Locator.GetPlayerCamera();
            var effects = camera != null ? camera.GetComponent<PlayerCameraEffectController>() : null;
            if (effects == null)
            {
                return;
            }

            if (blind && !_eyesForcedShut)
            {
                _eyesForcedShut = true;
                effects.CloseEyes(0.5f);
            }
            else if (!blind && _eyesForcedShut)
            {
                _eyesForcedShut = false;
                effects.OpenEyes(1f);
            }
        }

        private void LockLocalPlayer()
        {
            var controller = Locator.GetPlayerController();
            if (controller != null)
            {
                controller.LockMovement();
            }
        }

        private void UnlockLocalPlayer()
        {
            var controller = Locator.GetPlayerController();
            if (controller != null)
            {
                controller.UnlockMovement();
            }
        }

        private static void SetRemoteVisible(uint id, bool visible)
        {
            try
            {
                if (QSBPlayerManager.PlayerExists(id))
                {
                    var player = QSBPlayerManager.GetPlayer(id);
                    if (player != null && !player.IsLocalPlayer)
                    {
                        player.SetVisible(visible, 0.4f);
                    }
                }
            }
            catch
            {
                // player is doing frame perfects (mid join or leave)
            }
        }

        /// <summary>
        /// Players spawn without a suit, which means instant suffocation on every airless planet.
        /// Put the suit (and helmet) on so oxygen works the way it does for a normal run.
        /// </summary>
        private static void EnsureSuit()
        {
            var suit = Locator.GetPlayerSuit();
            if (suit != null && !suit.IsWearingSuit(false))
            {
                suit.SuitUp(false, true, true);
            }
        }

        /// <summary>Clears impact-sensor / hazard leftovers so the next hit can kill us again.</summary>
        private static void ResetPlayerComponents()
        {
            var body = Locator.GetPlayerBody();
            if (body == null)
            {
                return;
            }

            var sensor = body.GetComponent<HighSpeedImpactSensor>();
            if (sensor != null)
            {
                sensor._dead = false;
                sensor._dieNextUpdate = false;
            }

            var resources = body.GetComponent<PlayerResources>();
            if (resources != null)
            {
                resources._isSuffocating = false;
                resources.DebugRefillResources();
            }
        }

        // -------------------------------------------------------------------------------------
        // Dying / being found / respawning
        // -------------------------------------------------------------------------------------

        /// <summary>
        /// Called by the KillPlayer patch. While a round is running we take over death handling:
        /// no loop reset, no QSB respawn map, just a short black-out and a warp back after RespawnSeconds.
        /// </summary>
        public bool TryInterceptDeath(DeathType deathType)
        {
            if (_api == null || !RoundIsLive || !_roles.ContainsKey(LocalId))
            {
                return false;
            }

            if (_isOut)
            {
                return true;
            }

            // A hider dying for ANY reason becomes a seeker, same as being touched,
            // except during the grace period (countdown + the first DeathGraceSeconds of the round)
            // so spawn mishaps don't instantly convert people. Being touched by a seeker is never graced.
            var graceOver = Phase == GamePhase.Seeking && Time.unscaledTime - _goTime >= Round.DeathGraceSeconds;
            if (LocalRole == PlayerRole.Hider && graceOver)
            {
                BeginOut("You died!\nYou are now a SEEKER.");
                var seconds = Time.unscaledTime - _goTime;
                Send("CAUGHT|" + LocalId + "|" + seconds.ToString("0.00", CultureInfo.InvariantCulture));
            }
            else
            {
                BeginOut(LocalRole == PlayerRole.Hider ? "You died!\nYou're safe for now (grace period)." : "You died!");
            }

            return true;
        }

        private void BeginOut(string text)
        {
            if (_isOut)
            {
                return;
            }

            _isOut = true;
            _outText = text;
            _outUntil = Time.unscaledTime + Mathf.Max(0.5f, Round.RespawnSeconds);

            LockLocalPlayer();
            SetBlind(true);

            // Everybody else sees us vanish while we're out.
            Send("OUT", uint.MaxValue, false);
        }

        private void LocalTick()
        {
            if (RoundIsLive && _barrierBody != null && _roles.ContainsKey(LocalId))
            {
                EnforceBarrier();
            }

            if (_isOut)
            {
                if (Time.unscaledTime >= _outUntil)
                {
                    FinishLocalRespawn(teleport: RoundIsLive);
                }

                return;
            }

            if (!RoundIsLive || !_roles.ContainsKey(LocalId))
            {
                return;
            }

            // Seekers stay frozen until the countdown is over, even if something else unlocks them (hackers!!!)
            if (Phase == GamePhase.Countdown && LocalRole == PlayerRole.Seeker)
            {
                var controller = Locator.GetPlayerController();
                if (controller != null && !controller._isMovementLocked)
                {
                    controller.LockMovement();
                }
            }

            if (Phase == GamePhase.Seeking)
            {
                CheckTouches();
            }
        }

        /// <summary>Keeps the player (and the ship they're flying) inside a sphere around the planet.</summary>
        private void EnforceBarrier()
        {
            var limit = _barrierRadius + Mathf.Max(10f, Round.BarrierMeters);
            var playerBody = Locator.GetPlayerBody();

            if (playerBody != null && !_isOut)
            {
                var distance = Vector3.Distance(playerBody.GetPosition(), _barrierBody.GetPosition());
                var carried = PlayerState.IsInsideShuttle();

                // Carried off by something we can't clamp: don't fight it, just send the player back.
                if ((distance > limit + HardEscapeMeters || (carried && distance > limit))
                    && Time.unscaledTime >= _nextBarrierReturn)
                {
                    _nextBarrierReturn = Time.unscaledTime + 1f;
                    Toast("You left the play area - returning you to the planet.", 3f);
                    Planets.TeleportLocalPlayer(PlanetIndex);
                    return;
                }
            }

            ClampToBarrier(playerBody, limit);

            if (PlayerState.IsInsideShip())
            {
                ClampToBarrier(Locator.GetShipBody(), limit);
            }
        }

        private void ClampToBarrier(OWRigidbody body, float limit)
        {
            if (body == null)
            {
                return;
            }

            var centre = _barrierBody.GetPosition();
            var position = body.GetPosition();
            var offset = position - centre;
            var distance = offset.magnitude;
            if (distance <= limit || distance < 0.01f)
            {
                return;
            }

            var normal = offset / distance;
            body.SetPosition(centre + normal * (limit - 0.25f));

            // Kill the outward part of the velocity (relative to the planet) so it behaves like a wall, not a trampoline.
            var relative = body.GetVelocity() - _barrierBody.GetPointVelocity(position);
            var outward = Vector3.Dot(relative, normal);
            if (outward > 0f)
            {
                body.AddVelocityChange(-normal * outward);
            }

            if (Time.unscaledTime > _barrierWarnUntil)
            {
                _barrierWarnUntil = Time.unscaledTime + 2f;
                Toast("You can't leave the planet!", 2f);
            }
        }

        private void FinishLocalRespawn(bool teleport)
        {
            _isOut = false;

            if (teleport && PlanetIndex >= 0)
            {
                Planets.TeleportLocalPlayer(PlanetIndex);
            }

            // The game's own "wake up after dying in the dream world" event: opens the eyes,
            // restores health and re-enables movement/audio without touching the loop.
            GlobalMessenger.FireEvent("PlayerResurrection");
            _eyesForcedShut = false; // the resurrection event opens the eyes itself
            ResetPlayerComponents();
            EnsureSuit();

            // Seekers must stay blind & frozen if the countdown is still running (they died early).
            if (Phase == GamePhase.Countdown && LocalRole == PlayerRole.Seeker)
            {
                LockLocalPlayer();
                SetBlind(true);
            }
            else
            {
                UnlockLocalPlayer();
            }

            Send("BACK", uint.MaxValue, false);
        }

        // -------------------------------------------------------------------------------------
        // Touch detection
        // -------------------------------------------------------------------------------------

        private void CheckTouches()
        {
            var playerBody = Locator.GetPlayerBody();
            if (playerBody == null)
            {
                return;
            }

            var myPosition = playerBody.GetPosition();
            var reach = Round.TouchDistance;
            var role = LocalRole;
            var now = Time.unscaledTime;

            foreach (var other in _roles)
            {
                var id = other.Key;
                if (id == LocalId || other.Value == role || _outPlayers.Contains(id))
                {
                    continue;
                }

                GameObject body;
                try
                {
                    if (!_api.GetPlayerReady(id))
                    {
                        continue;
                    }

                    body = _api.GetPlayerBody(id);
                }
                catch
                {
                    continue;
                }

                if (body == null)
                {
                    continue;
                }

                if (Vector3.Distance(myPosition, body.transform.position) > reach)
                {
                    continue;
                }

                // Hider touched by a seeker -> tell everyone. Both sides report it; duplicates are ignored.
                var victim = role == PlayerRole.Hider ? LocalId : id;
                if (_lastTagSent.TryGetValue(victim, out var last) && now - last < 1f)
                {
                    continue;
                }

                _lastTagSent[victim] = now;
                var seconds = now - _goTime;
                Send("CAUGHT|" + victim + "|" + seconds.ToString("0.00", CultureInfo.InvariantCulture));
            }
        }

        // -------------------------------------------------------------------------------------
        // Nametag / map-marker rules (used by the Harmony patches)
        // -------------------------------------------------------------------------------------

        // decides who can see who
        public bool CanSeeMarker(uint targetId, bool forMap)
        {
            if (_api == null || !RoundIsLive)
            {
                return true;
            }

            var me = LocalRole;
            var them = GetRole(targetId);
            if (me == PlayerRole.None || them == PlayerRole.None || targetId == LocalId)
            {
                return true;
            }

            if (_outPlayers.Contains(targetId))
            {
                return false;
            }

            if (me == PlayerRole.Seeker)
            {
                // Seekers see each other, never the hiders.
                return them == PlayerRole.Seeker;
            }

            // Local player is a hider.
            if (them == PlayerRole.Seeker)
            {
                return Round.HidersSeeSeekerNametags;
            }

            if (forMap)
            {
                return false;
            }

            try
            {
                var body = _api.GetPlayerBody(targetId);
                var camera = Locator.GetPlayerCamera();
                if (body == null || camera == null)
                {
                    return false;
                }

                return Vector3.Distance(camera.transform.position, body.transform.position) <= Round.NametagRevealDistance;
            }
            catch
            {
                return false;
            }
        }

        public bool IsMinimapHidden() => RoundIsLive && _roles.ContainsKey(LocalId);

        /// <summary>True while the local player is in a running round (used to lock out vehicles/warps).</summary>
        public bool IsInActiveRound => RoundIsLive && _roles.ContainsKey(LocalId);

        public bool IsLocalOut => _isOut;
    }
}
