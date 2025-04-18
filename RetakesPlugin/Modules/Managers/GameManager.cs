﻿using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace RetakesPlugin.Modules.Managers;

public class GameManager
{
    private readonly Translator _translator;
    private Dictionary<int, int> _playerRoundScores = new();
    public readonly QueueManager QueueManager;
    private readonly int _consecutiveRoundWinsToScramble;
    private readonly bool _isScrambleEnabled;
    private readonly bool _removeSpectatorsEnabled;
    private readonly bool _isBalanceEnabled;
    public const int ScoreForKill = 50;
    public const int ScoreForAssist = 25;
    public const int ScoreForDefuse = 50;

    public GameManager(Translator translator, QueueManager queueManager, int? roundsToScramble, bool? isScrambleEnabled, bool? removeSpectatorsEnabled, bool? isBalanceEnabled)
    {
        _translator = translator;
        QueueManager = queueManager;
        _consecutiveRoundWinsToScramble = roundsToScramble ?? 5;
        _isScrambleEnabled = isScrambleEnabled ?? true;
        _removeSpectatorsEnabled = removeSpectatorsEnabled ?? false;
        _isBalanceEnabled = isBalanceEnabled ?? true;
    }

    private bool _scrambleNextRound;

    public void ScrambleNextRound(CCSPlayerController? admin = null)
    {
        _scrambleNextRound = true;
        foreach (var player in Utilities.GetPlayers().Where(x => x.IsValid))
        {
            var msg = _translator[player, "retakes.teams.admin_scramble", admin?.PlayerName ?? "The server owner"];
            player.PrintToChat($"{RetakesPlugin.MessagePrefix}{msg}");
        }
    }

    private void ScrambleTeams()
    {
        _scrambleNextRound = false;
        _consecutiveRoundsWon = 0;

        var shuffledActivePlayers = Helpers.Shuffle(QueueManager.ActivePlayers);

        var newTerrorists = shuffledActivePlayers.Take(QueueManager.GetTargetNumTerrorists()).ToList();
        var newCounterTerrorists = shuffledActivePlayers.Except(newTerrorists).ToList();

        SetTeams(newTerrorists, newCounterTerrorists);
    }

    public void ResetPlayerScores()
    {
        _playerRoundScores = new Dictionary<int, int>();
    }

    public void AddScore(CCSPlayerController player, int score)
    {
        if (!Helpers.IsValidPlayer(player) || player.UserId == null)
        {
            return;
        }

        var playerId = (int)player.UserId;

        if (!_playerRoundScores.TryAdd(playerId, score))
        {
            // Add to the player's existing score
            _playerRoundScores[playerId] += score;
        }
    }

    private int _consecutiveRoundsWon;

    private void TerroristRoundWin()
    {
        _consecutiveRoundsWon++;

        var shouldScrambleNow = _isScrambleEnabled && _consecutiveRoundsWon == _consecutiveRoundWinsToScramble;
        var roundsLeftToScramble = _consecutiveRoundWinsToScramble - _consecutiveRoundsWon;
        // Almost scramble if 1-2 rounds left to automatic scramble
        var shouldAlmostScramble = _isScrambleEnabled && roundsLeftToScramble > 0 && roundsLeftToScramble <= 2;

        if (shouldScrambleNow)
        {
            foreach (var player in Utilities.GetPlayers().Where(x => x.IsValid))
            {
                var msg = _translator[player, "retakes.teams.scramble", _consecutiveRoundWinsToScramble];
                player.PrintToChat($"{RetakesPlugin.MessagePrefix}{msg}");
            }
            ScrambleTeams();
        }
        else if (shouldAlmostScramble)
        {
            foreach (var player in Utilities.GetPlayers().Where(x => x.IsValid))
            {
                var msg = _translator[player, "retakes.teams.almost_scramble", _consecutiveRoundsWon, roundsLeftToScramble];
                player.PrintToChat($"{RetakesPlugin.MessagePrefix}{msg}");
            }
        }
        else if (_consecutiveRoundsWon >= 3)
        {
            foreach (var player in Utilities.GetPlayers().Where(x => x.IsValid))
            {
                var msg = _translator[player, "retakes.teams.win_streak", _consecutiveRoundWinsToScramble];
                player.PrintToChat($"{RetakesPlugin.MessagePrefix}{msg}");
            }
        }
    }

    private void CounterTerroristRoundWin()
    {
        if (_consecutiveRoundsWon >= 3)
        {
            foreach (var player in Utilities.GetPlayers().Where(x => x.IsValid))
            {
                var msg = _translator[player, "retakes.teams.win_streak_over", _consecutiveRoundsWon];
                player.PrintToChat($"{RetakesPlugin.MessagePrefix}{msg}");
            }
        }

        _consecutiveRoundsWon = 0;

        var targetNumTerrorists = QueueManager.GetTargetNumTerrorists();
        var sortedCounterTerroristPlayers = GetSortedActivePlayers(CsTeam.CounterTerrorist);

        // Ensure that the players with the scores are set as new terrorists first.
        var newTerrorists = sortedCounterTerroristPlayers.Where(player => player.Score > 0).Take(targetNumTerrorists)
            .ToList();

        if (newTerrorists.Count < targetNumTerrorists)
        {
            // Shuffle the other players with 0 score to ensure it's random who is swapped
            var playersLeft = Helpers.Shuffle(sortedCounterTerroristPlayers.Except(newTerrorists).ToList());
            newTerrorists.AddRange(playersLeft.Take(targetNumTerrorists - newTerrorists.Count));
        }

        if (newTerrorists.Count < targetNumTerrorists)
        {
            // If we still don't have enough terrorists
            newTerrorists.AddRange(
                GetSortedActivePlayers(CsTeam.Terrorist)
                    .Take(targetNumTerrorists - newTerrorists.Count)
            );
        }

        newTerrorists.AddRange(sortedCounterTerroristPlayers.Where(player => player.Score > 0)
            .Take(targetNumTerrorists - newTerrorists.Count).ToList());

        var newCounterTerrorists = QueueManager.ActivePlayers.Except(newTerrorists).ToList();

        SetTeams(newTerrorists, newCounterTerrorists);
    }

    private void BalanceTeams()
    {
        List<CCSPlayerController> newTerrorists = [];
        List<CCSPlayerController> newCounterTerrorists = [];

        var currentNumTerrorist = Helpers.GetCurrentNumPlayers(CsTeam.Terrorist);
        var numTerroristsNeeded = QueueManager.GetTargetNumTerrorists() - currentNumTerrorist;

        if (numTerroristsNeeded > 0)
        {
            var sortedCounterTerroristPlayers = GetSortedActivePlayers(CsTeam.CounterTerrorist);

            newTerrorists = sortedCounterTerroristPlayers.Where(player => player.Score > 0)
                .Take(numTerroristsNeeded).ToList();

            if (newTerrorists.Count < numTerroristsNeeded)
            {
                var playersLeft = Helpers.Shuffle(sortedCounterTerroristPlayers.Except(newTerrorists).ToList());
                newTerrorists.AddRange(playersLeft.Take(numTerroristsNeeded - newTerrorists.Count));
            }
        }

        var currentNumCounterTerroristAfterBalance = Helpers.GetCurrentNumPlayers(CsTeam.CounterTerrorist);
        var numCounterTerroristsNeeded =
            QueueManager.GetTargetNumCounterTerrorists() - currentNumCounterTerroristAfterBalance;

        if (numCounterTerroristsNeeded > 0)
        {
            var terroristsWithZeroScore = QueueManager.ActivePlayers
                .Where(player =>
                    Helpers.IsValidPlayer(player)
                    && player.Team == CsTeam.Terrorist
                    && _playerRoundScores.GetValueOrDefault((int)player.UserId!, 0) == 0
                )
                .Except(newTerrorists)
                .ToList();

            // Shuffle to avoid repetitive swapping of the same players
            newCounterTerrorists = Helpers.Shuffle(terroristsWithZeroScore).Take(numCounterTerroristsNeeded).ToList();

            if (numCounterTerroristsNeeded > newCounterTerrorists.Count)
            {
                // For remaining excess terrorists, move the ones with the lowest score to CT
                newCounterTerrorists.AddRange(
                    QueueManager.ActivePlayers
                        .Except(newCounterTerrorists)
                        .Except(newTerrorists)
                        .Where(player => Helpers.IsValidPlayer(player) && player.Team == CsTeam.Terrorist)
                        .OrderBy(player => _playerRoundScores.GetValueOrDefault((int)player.UserId!, 0))
                        .Take(numTerroristsNeeded - newCounterTerrorists.Count)
                        .ToList()
                );
            }
        }

        SetTeams(newTerrorists, newCounterTerrorists);
    }

    public void OnRoundPreStart(CsTeam winningTeam)
    {
        // Handle team swaps during round pre-start.
        switch (winningTeam)
        {
            case CsTeam.CounterTerrorist:
                if (_isBalanceEnabled)
                {
                    CounterTerroristRoundWin();
                }
                break;

            case CsTeam.Terrorist:
                TerroristRoundWin();
                break;
        }

        if (_scrambleNextRound)
        {
            ScrambleTeams();
        }

        if (_isBalanceEnabled)
        {
            BalanceTeams();
        }
    }

    private List<CCSPlayerController> GetSortedActivePlayers(CsTeam? team = null)
    {
        return QueueManager.ActivePlayers
            .Where(Helpers.IsValidPlayer)
            .Where(player => team == null || player.Team == team)
            .OrderByDescending(player => _playerRoundScores.GetValueOrDefault((int)player.UserId!, 0))
            .ToList();
    }

    private void SetTeams(List<CCSPlayerController>? terrorists, List<CCSPlayerController>? counterTerrorists)
    {
        terrorists ??= [];
        counterTerrorists ??= [];

        foreach (var player in QueueManager.ActivePlayers.Where(Helpers.IsValidPlayer))
        {
            if (terrorists.Contains(player))
            {
                player.SwitchTeam(CsTeam.Terrorist);
            }
            else if (counterTerrorists.Contains(player))
            {
                player.SwitchTeam(CsTeam.CounterTerrorist);
            }
        }
    }

    public HookResult RemoveSpectators(EventPlayerTeam @event, HashSet<CCSPlayerController> _hasMutedVoices)
    {
        if (_removeSpectatorsEnabled)
        {
            CCSPlayerController? player = @event.Userid;

            if (!Helpers.IsValidPlayer(player))
            {
                return HookResult.Continue;
            }
            int team = @event.Team;

            if (team == (int)CsTeam.Spectator)
            {
                // Ensure player is active ingame.
                if (QueueManager.ActivePlayers.Contains(player))
                {
                    QueueManager.RemovePlayerFromQueues(player);
                    _hasMutedVoices.Remove(player);
                }
            }
        }
        return HookResult.Continue;
    }
}
