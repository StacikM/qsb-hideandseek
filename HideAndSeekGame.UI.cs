using System.Linq;
using UnityEngine;

namespace QSBHideAndSeek
{
    public partial class HideAndSeekGame
    {
        private GUIStyle _titleStyle;
        private GUIStyle _bodyStyle;
        private GUIStyle _bigStyle;
        private int _styleHeight;

        private void EnsureStyles()
        {
            if (_titleStyle != null && _styleHeight == Screen.height)
            {
                return;
            }

            _styleHeight = Screen.height;
            var scale = Screen.height / 1080f;

            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(34 * scale),
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                wordWrap = true,
            };
            _bodyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(24 * scale),
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
            };
            _bigStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(120 * scale),
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
            };
        }

        private static void Shadowed(Rect rect, string text, GUIStyle style, Color color)
        {
            var old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.85f);
            GUI.Label(new Rect(rect.x + 2, rect.y + 2, rect.width, rect.height), text, style);
            GUI.color = color;
            GUI.Label(rect, text, style);
            GUI.color = old;
        }

        private void OnGUI()
        {
            if (_api == null || !_inSolarSystem)
            {
                return;
            }

            // IMGUI colours leak between scripts' OnGUI calls; make sure another mod's tint can't hide our overlay.
            GUI.depth = -10000;
            GUI.color = Color.white;
            GUI.contentColor = Color.white;
            GUI.backgroundColor = Color.white;

            EnsureStyles();
            var w = Screen.width;
            var h = Screen.height;
            var now = Time.unscaledTime;

            var seekerBlind = Phase == GamePhase.Countdown && LocalRole == PlayerRole.Seeker && _roles.ContainsKey(LocalId);

            if (_isOut || seekerBlind)
            {
                DrawBlackout(w, h, now, seekerBlind);
                return;
            }

            switch (Phase)
            {
                case GamePhase.Voting:
                    DrawVote(w, h, now);
                    break;
                case GamePhase.Countdown:
                    DrawHiderCountdown(w, now);
                    break;
                case GamePhase.Seeking:
                    DrawSeeking(w, now);
                    break;
            }

            if (_results != null)
            {
                if (now > _resultsUntil)
                {
                    _results = null;
                }
                else
                {
                    DrawResults(w, h);
                }
            }

            if (now < _toastUntil && !string.IsNullOrEmpty(_toast))
            {
                Shadowed(new Rect(0, h * 0.18f, w, 50), _toast, _bodyStyle, Color.white);
            }
        }

        private void DrawBlackout(int w, int h, float now, bool seekerBlind)
        {
            GUI.DrawTexture(new Rect(0, 0, w, h), Texture2D.blackTexture);

            if (_isOut)
            {
                var left = Mathf.Max(0f, _outUntil - now);
                Shadowed(new Rect(0, h * 0.30f, w, 150), _outText, _titleStyle, Color.white);
                Shadowed(new Rect(0, h * 0.50f, w, 60), $"Respawning in {Mathf.CeilToInt(left)}...", _bodyStyle, Color.gray);
                return;
            }

            if (seekerBlind)
            {
                var left = Mathf.Max(0f, Round.CountdownSeconds - (now - _phaseStart));
                Shadowed(new Rect(0, h * 0.18f, w, 60), "You are a SEEKER", _titleStyle, new Color(1f, 0.35f, 0.35f));
                Shadowed(new Rect(0, h * 0.30f, w, 220), Mathf.CeilToInt(left).ToString(), _bigStyle, Color.white);
                Shadowed(new Rect(0, h * 0.62f, w, 60), "The hiders are hiding. You can start seeking when the countdown ends.", _bodyStyle, Color.gray);
            }
        }

        private void DrawVote(int w, int h, float now)
        {
            var left = Mathf.Max(0f, Round.VoteSeconds - (now - _phaseStart));
            Shadowed(new Rect(0, h * 0.10f, w, 50), "HIDE & SEEK: Vote for a planet", _titleStyle, Color.white);

            if (!_participants.Contains(LocalId))
            {
                Shadowed(new Rect(0, h * 0.17f, w, 40), "A vote is running. You'll be able to join the next round.", _bodyStyle, Color.gray);
                return;
            }

            Shadowed(new Rect(0, h * 0.17f, w, 40),
                $"Press the number key of the planet you want. Voting ends in {Mathf.CeilToInt(left)}s", _bodyStyle, Color.gray);

            _votes.TryGetValue(LocalId, out var mine);
            var voted = _votes.ContainsKey(LocalId);

            for (var i = 0; i < Planets.Options.Length; i++)
            {
                var count = _votes.Values.Count(v => v == i);
                var isMine = voted && mine == i;
                var color = isMine ? new Color(0.4f, 1f, 0.5f) : Color.white;
                var text = $"[{i + 1}] {Planets.Options[i].DisplayName}   -   {count} vote{(count == 1 ? "" : "s")}{(isMine ? "   (your vote)" : "")}";
                Shadowed(new Rect(0, h * 0.25f + i * 42f * (h / 1080f), w, 40), text, _bodyStyle, color);
            }

            var footer = $"{_votes.Count}/{_participants.Count} voted";
            if (IsHost)
            {
                footer += $"   |   Press {MenuKey} to cancel";
            }

            Shadowed(new Rect(0, h * 0.25f + Planets.Options.Length * 42f * (h / 1080f) + 20, w, 40), footer, _bodyStyle, Color.gray);
        }

        private void DrawHiderCountdown(int w, float now)
        {
            if (!_roles.ContainsKey(LocalId))
            {
                return;
            }

            var left = Mathf.Max(0f, Round.CountdownSeconds - (now - _phaseStart));
            var plural = _roles.Count(x => x.Value == PlayerRole.Seeker) == 1 ? "seeker is" : "seekers are";
            Shadowed(new Rect(0, 30, w, 50), "You are a HIDER - hide!", _titleStyle, new Color(0.5f, 0.85f, 1f));
            Shadowed(new Rect(0, 80, w, 40), $"The {plural} released in {Mathf.CeilToInt(left)}s", _bodyStyle, Color.white);
        }

        private void DrawSeeking(int w, float now)
        {
            if (!_roles.ContainsKey(LocalId))
            {
                return;
            }

            var hidersLeft = _roles.Count(x => x.Value == PlayerRole.Hider);
            var elapsed = now - _goTime;

            var title = LocalRole == PlayerRole.Seeker
                ? "You are a SEEKER - touch the hiders!"
                : "You are a HIDER - don't get touched!";
            var color = LocalRole == PlayerRole.Seeker ? new Color(1f, 0.35f, 0.35f) : new Color(0.5f, 0.85f, 1f);

            Shadowed(new Rect(0, 30, w, 50), title, _titleStyle, color);
            Shadowed(new Rect(0, 80, w, 40), $"Hiders left: {hidersLeft}   |   Time: {FormatTime(elapsed)}", _bodyStyle, Color.white);
        }

        private void DrawResults(int w, int h)
        {
            var hiders = _results.Where(x => x.WasHider).ToList();
            var seekers = _results.Where(x => !x.WasHider).ToList();
            var rowH = 38f * (h / 1080f);
            var boxW = Mathf.Min(w * 0.7f, 900f);
            var boxH = rowH * (hiders.Count + seekers.Count + 5) + 20;
            var box = new Rect((w - boxW) / 2f, h * 0.12f, boxW, boxH);

            GUI.color = new Color(0f, 0f, 0f, 0.8f);
            GUI.DrawTexture(box, Texture2D.whiteTexture);
            GUI.color = Color.white;

            var y = box.y + 10;
            Shadowed(new Rect(box.x, y, boxW, rowH + 10), "HIDE & SEEK - RESULTS", _titleStyle, Color.white);
            y += rowH + 14;
            Shadowed(new Rect(box.x, y, boxW, rowH), $"Round length: {FormatTime(_roundSeconds)}   |   Time spent hiding:", _bodyStyle, Color.gray);
            y += rowH + 6;

            for (var i = 0; i < hiders.Count; i++)
            {
                var color = i == 0 ? new Color(1f, 0.85f, 0.2f) : Color.white;
                var crown = i == 0 ? "  (winner)" : "";
                Shadowed(new Rect(box.x, y, boxW, rowH), $"{i + 1}. {hiders[i].Name}  -  {FormatTime(hiders[i].Seconds)}{crown}", _bodyStyle, color);
                y += rowH;
            }

            if (seekers.Count > 0)
            {
                var names = string.Join(", ", seekers.Select(x => x.Name).ToArray());
                Shadowed(new Rect(box.x, y + 6, boxW, rowH), $"Started as seeker: {names}", _bodyStyle, Color.gray);
            }
        }
    }
}
