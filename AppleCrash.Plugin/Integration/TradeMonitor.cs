namespace AppleCrash.Plugin.Integration;

/// <summary>Trade capability placeholder that fails closed until completion is provable.</summary>
public sealed class TradeMonitor : IDisposable
{
    public bool AutomaticTradeVerified => false;

    public string CapabilityReason { get; } =
        "Disabled: installed AgentTrade exposes UI/item-cache operations but no verified typed gil amounts, counterparty identity, and completed-trade lifecycle. Manual ledger entry is required.";

    public void Dispose()
    {
    }
}
