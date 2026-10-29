using AppleCrash.Core;
using Xunit;
namespace AppleCrash.Tests;
public class CoreTests {
    private static (GameEngine e, string id) Setup(string d) { var e = new GameEngine(d, "host/world"); var p = e.AddPlayer("A", "W"); e.ConfigurePlayer(p.Id, 100, true); e.SetBalance(p.Id, 1000, "seed"); return (e, p.Id); }
    [Fact] public void StartDebitsAndCashoutIncludesStake() { var (e,id)=Setup(Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString())); e.StartRound(); e.CorrectMultiplier(2.5m,"test"); e.CashOut(id); Assert.Equal(1150,e.State.Players[0].Balance); Assert.Equal(250,e.State.Round!.Participants[0].Payout); }
    [Fact] public void StopParserIsWholeWordAndGlyphsNormalize() { Assert.True(ChatParser.ContainsStop("please STOP!")); Assert.False(ChatParser.ContainsStop("stopping")); Assert.False(ChatParser.ContainsStop("nonstop")); Assert.Equal("roll 54",ChatParser.NormalizeDigits("roll \ue065\ue064")); }
    [Fact] public void ThresholdEqualityPassesAndCrashNeedsFinish() { var (e,id)=Setup(Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString())); e.SaveSettings(new PluginSettings { Steps=[new(){From=1,To=2,Threshold=5},new(){From=2,To=20,Threshold=5}] }); e.StartRound(); e.ArmNextStep(); e.AcceptRoll(5); Assert.Equal(2,e.State.Round!.Multiplier); e.ArmNextStep(); e.AcceptRoll(1); Assert.Equal(RoundPhase.CrashReview,e.State.Round.Phase); Assert.Equal(ParticipantStatus.InPlay,e.State.Round.Participants[0].Status); e.FinishCrash("settle"); Assert.Equal(ParticipantStatus.Lost,e.State.Round.Participants[0].Status); }
    [Fact] public void PersistsAcrossReloadAndHostIsolated() { var d=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString()); var (e,id)=Setup(d); var e2=new GameEngine(d,"host/world"); Assert.Equal(id,e2.State.Players[0].Id); var other=new GameEngine(d,"other"); Assert.NotNull(other.StorageError); Assert.Empty(other.State.Players); }
}
