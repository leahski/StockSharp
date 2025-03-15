﻿namespace StockSharp.Samples.Strategies.LiveArbitrage;

using System;
using System.Collections.Generic;
using System.Linq;

using StockSharp.Algo;
using StockSharp.Algo.Strategies;
using StockSharp.BusinessEntities;
using StockSharp.Messages;

/// <summary>
/// Strategy that performs arbitrage between two related instruments (e.g., future and underlying stock).
/// </summary>
public class ArbitrageStrategy : Strategy
{
	private enum ArbitrageState
	{
		Contango,
		Backvordation,
		None,
		OrderRegistration,
	}

	// Parameters for the strategy
	private readonly StrategyParam<Security> _futureSecurity;
	private readonly StrategyParam<Security> _stockSecurity;
	private readonly StrategyParam<Portfolio> _futurePortfolio;
	private readonly StrategyParam<Portfolio> _stockPortfolio;
	private readonly StrategyParam<decimal> _stockMultiplicator;
	private readonly StrategyParam<decimal> _futureVolume;
	private readonly StrategyParam<decimal> _stockVolume;
	private readonly StrategyParam<decimal> _profitToExit;
	private readonly StrategyParam<decimal> _spreadToGenerateSignal;

	/// <summary>
	/// Future security.
	/// </summary>
	public Security FutureSecurity
	{
		get => _futureSecurity.Value;
		set => _futureSecurity.Value = value;
	}

	/// <summary>
	/// Stock security.
	/// </summary>
	public Security StockSecurity
	{
		get => _stockSecurity.Value;
		set => _stockSecurity.Value = value;
	}

	/// <summary>
	/// Portfolio for future trades.
	/// </summary>
	public Portfolio FuturePortfolio
	{
		get => _futurePortfolio.Value;
		set => _futurePortfolio.Value = value;
	}

	/// <summary>
	/// Portfolio for stock trades.
	/// </summary>
	public Portfolio StockPortfolio
	{
		get => _stockPortfolio.Value;
		set => _stockPortfolio.Value = value;
	}

	/// <summary>
	/// Stock multiplicator (e.g., lot size).
	/// </summary>
	public decimal StockMultiplicator
	{
		get => _stockMultiplicator.Value;
		set => _stockMultiplicator.Value = value;
	}

	/// <summary>
	/// Future volume to trade.
	/// </summary>
	public decimal FutureVolume
	{
		get => _futureVolume.Value;
		set => _futureVolume.Value = value;
	}

	/// <summary>
	/// Stock volume to trade.
	/// </summary>
	public decimal StockVolume
	{
		get => _stockVolume.Value;
		set => _stockVolume.Value = value;
	}

	/// <summary>
	/// Profit threshold to exit position.
	/// </summary>
	public decimal ProfitToExit
	{
		get => _profitToExit.Value;
		set => _profitToExit.Value = value;
	}

	/// <summary>
	/// Spread threshold to generate entry signal.
	/// </summary>
	public decimal SpreadToGenerateSignal
	{
		get => _spreadToGenerateSignal.Value;
		set => _spreadToGenerateSignal.Value = value;
	}

	private ArbitrageState _currentState = ArbitrageState.None;
	private decimal _enterSpread;
	private Func<decimal> _arbitragePnl;
	private decimal _profit;
	private decimal _futBid;
	private decimal _futAck;
	private decimal _stBid;
	private decimal _stAsk;
	private IOrderBookMessage _lastFut;
	private IOrderBookMessage _lastSt;
	private SecurityId _futId, _stockId;

	/// <summary>
	/// Initializes a new instance of the <see cref="ArbitrageStrategy"/>.
	/// </summary>
	public ArbitrageStrategy()
	{
		_futureSecurity = Param<Security>(nameof(FutureSecurity), null)
			.SetDisplay("Future Security", "Security representing the future", "Instruments");

		_stockSecurity = Param<Security>(nameof(StockSecurity), null)
			.SetDisplay("Stock Security", "Security representing the underlying stock", "Instruments");

		_futurePortfolio = Param<Portfolio>(nameof(FuturePortfolio), null)
			.SetDisplay("Future Portfolio", "Portfolio for future trades", "Portfolios");

		_stockPortfolio = Param<Portfolio>(nameof(StockPortfolio), null)
			.SetDisplay("Stock Portfolio", "Portfolio for stock trades", "Portfolios");

		_stockMultiplicator = Param(nameof(StockMultiplicator), 1m)
			.SetDisplay("Stock Multiplicator", "Stock price multiplicator (e.g., lot size)", "Settings");

		_futureVolume = Param(nameof(FutureVolume), 1m)
			.SetDisplay("Future Volume", "Volume for future trades", "Settings");

		_stockVolume = Param(nameof(StockVolume), 1m)
			.SetDisplay("Stock Volume", "Volume for stock trades", "Settings");

		_profitToExit = Param(nameof(ProfitToExit), 0.5m)
			.SetDisplay("Profit To Exit", "Profit threshold to exit position", "Settings");

		_spreadToGenerateSignal = Param(nameof(SpreadToGenerateSignal), 1m)
			.SetDisplay("Spread To Entry", "Spread threshold to generate entry signal", "Settings");
	}

	/// <summary>
	/// Strategy startup initialization.
	/// </summary>
	protected override void OnStarted(DateTimeOffset time)
	{
		base.OnStarted(time);

		if (FutureSecurity == null)
			throw new InvalidOperationException("Future security is not specified.");

		if (StockSecurity == null)
			throw new InvalidOperationException("Stock security is not specified.");

		if (FuturePortfolio == null)
			throw new InvalidOperationException("Future portfolio is not specified.");

		if (StockPortfolio == null)
			throw new InvalidOperationException("Stock portfolio is not specified.");

		_futId = FutureSecurity.ToSecurityId();
		_stockId = StockSecurity.ToSecurityId();

		var futureDepthSubscription = this.SubscribeMarketDepth(FutureSecurity);
		var stockDepthSubscription = this.SubscribeMarketDepth(StockSecurity);

		futureDepthSubscription.WhenOrderBookReceived(this).Do(ProcessMarketDepth).Apply(this);
		stockDepthSubscription.WhenOrderBookReceived(this).Do(ProcessMarketDepth).Apply(this);
	}

	/// <summary>
	/// Processes market depth updates.
	/// </summary>
	private void ProcessMarketDepth(IOrderBookMessage depth)
	{
		// Update the latest market depth for each security
		if (depth.SecurityId == _futId)
			_lastFut = depth;
		else if (depth.SecurityId == _stockId)
			_lastSt = depth;

		// Wait until we have data for both securities
		if (_lastFut is null || _lastSt is null)
			return;

		// Calculate weighted average prices for specific volumes
		_futBid = GetAveragePrice(_lastFut, Sides.Sell, FutureVolume);
		_futAck = GetAveragePrice(_lastFut, Sides.Buy, FutureVolume);
		_stBid = GetAveragePrice(_lastSt, Sides.Sell, StockVolume) * StockMultiplicator;
		_stAsk = GetAveragePrice(_lastSt, Sides.Buy, StockVolume) * StockMultiplicator;

		// Check for valid prices
		if (_futBid == 0 || _futAck == 0 || _stBid == 0 || _stAsk == 0)
			return;

		// Calculate spreads
		var contangoSpread = _futBid - _stAsk;        // Future price > Stock price
		var backvordationSpread = _stBid - _futAck;   // Stock price > Future price

		decimal spread;
		ArbitrageState arbitrageSignal;

		// Determine which arbitrage opportunity is better
		if (backvordationSpread > contangoSpread)
		{
			arbitrageSignal = ArbitrageState.Backvordation;
			spread = backvordationSpread;
		}
		else
		{
			arbitrageSignal = ArbitrageState.Contango;
			spread = contangoSpread;
		}

		// Log current state and spreads
		LogInfo($"Current state {_currentState}, enter spread = {_enterSpread}");
		LogInfo($"{ArbitrageState.Backvordation} spread = {backvordationSpread}");
		LogInfo($"{ArbitrageState.Contango}        spread = {contangoSpread}");
		LogInfo($"Entry from spread:{SpreadToGenerateSignal}. Exit from profit:{ProfitToExit}");

		// Calculate current profit if we have an open position
		if (_arbitragePnl != null)
		{
			_profit = _arbitragePnl();
			LogInfo($"Profit: {_profit}");
		}

		// Process signals based on current state and market conditions
		ProcessSignals(arbitrageSignal, spread);
	}

	/// <summary>
	/// Processes strategy signals.
	/// </summary>
	private void ProcessSignals(ArbitrageState arbitrageSignal, decimal spread)
	{
		// Enter a new position when no position is open and spread exceeds threshold
		if (_currentState == ArbitrageState.None && spread > SpreadToGenerateSignal)
		{
			_currentState = ArbitrageState.OrderRegistration;

			if (arbitrageSignal == ArbitrageState.Backvordation)
			{
				ExecuteBackvardation();
			}
			else
			{
				ExecuteContango();
			}
		}
		// Exit Backvordation position when profit threshold is reached
		else if (_currentState == ArbitrageState.Backvordation && _profit >= ProfitToExit)
		{
			_currentState = ArbitrageState.OrderRegistration;
			CloseBackvardationPosition();
		}
		// Exit Contango position when profit threshold is reached
		else if (_currentState == ArbitrageState.Contango && _profit >= ProfitToExit)
		{
			_currentState = ArbitrageState.OrderRegistration;
			CloseContangoPosition();
		}
	}

	/// <summary>
	/// Executes Backvordation strategy (buy future, sell stock).
	/// </summary>
	private void ExecuteBackvardation()
	{
		var (buy, sell) = GenerateOrdersBackvardation();

		new IMarketRule[]
		{
			buy.WhenMatched(this),
			sell.WhenMatched(this),
			buy.WhenAllTrades(this),
			sell.WhenAllTrades(this),
		}
		.And()
		.Do(() =>
		{
			var futurePrise = buy.GetTrades(Connector).GetAveragePrice();
			var stockPrise = sell.GetTrades(Connector).GetAveragePrice() * StockMultiplicator;

			_enterSpread = stockPrise - futurePrise;

			_arbitragePnl = () => stockPrise - _stAsk + _futBid - futurePrise;
			_currentState = ArbitrageState.Backvordation;

		}).Once().Apply(this);

		RegisterOrder(buy);
		RegisterOrder(sell);
	}

	/// <summary>
	/// Executes Contango strategy (sell future, buy stock).
	/// </summary>
	private void ExecuteContango()
	{
		var (sell, buy) = GenerateOrdersContango();

		new IMarketRule[]
		{
			sell.WhenMatched(this),
			buy.WhenMatched(this),
			sell.WhenAllTrades(this),
			buy.WhenAllTrades(this),
		}
		.And()
		.Do(() =>
		{
			var futurePrise = sell.GetTrades(Connector).GetAveragePrice();
			var stockPrise = buy.GetTrades(Connector).GetAveragePrice() * StockMultiplicator;

			_enterSpread = futurePrise - stockPrise;

			_arbitragePnl = () => futurePrise - _futAck + _stBid - stockPrise;
			_currentState = ArbitrageState.Contango;

		}).Once().Apply(this);

		RegisterOrder(sell);
		RegisterOrder(buy);
	}

	/// <summary>
	/// Closes a Backvordation position.
	/// </summary>
	private void CloseBackvardationPosition()
	{
		var (sell, buy) = GenerateOrdersContango();

		new IMarketRule[]
		{
			sell.WhenMatched(this),
			buy.WhenMatched(this),
		}
		.And()
		.Do(() =>
		{
			_enterSpread = 0;
			_arbitragePnl = null;
			_currentState = ArbitrageState.None;
		}).Once().Apply(this);

		RegisterOrder(sell);
		RegisterOrder(buy);
	}

	/// <summary>
	/// Closes a Contango position.
	/// </summary>
	private void CloseContangoPosition()
	{
		var (buy, sell) = GenerateOrdersBackvardation();

		new IMarketRule[]
		{
			buy.WhenMatched(this),
			sell.WhenMatched(this),
		}
		.And()
		.Do(() =>
		{
			_enterSpread = 0;
			_arbitragePnl = null;
			_currentState = ArbitrageState.None;
		}).Once().Apply(this);

		RegisterOrder(buy);
		RegisterOrder(sell);
	}

	/// <summary>
	/// Generates orders for Backvordation strategy.
	/// </summary>
	private (Order buy, Order sell) GenerateOrdersBackvardation()
	{
		var futureBuy = CreateOrder(Sides.Buy, FutureVolume);
		futureBuy.Portfolio = FuturePortfolio;
		futureBuy.Security = FutureSecurity;
		futureBuy.Type = OrderTypes.Market;

		var stockSell = CreateOrder(Sides.Sell, StockVolume);
		stockSell.Portfolio = StockPortfolio;
		stockSell.Security = StockSecurity;
		stockSell.Type = OrderTypes.Market;

		return (futureBuy, stockSell);
	}

	/// <summary>
	/// Generates orders for Contango strategy.
	/// </summary>
	private (Order sell, Order buy) GenerateOrdersContango()
	{
		var futureSell = CreateOrder(Sides.Sell, FutureVolume);
		futureSell.Portfolio = FuturePortfolio;
		futureSell.Security = FutureSecurity;
		futureSell.Type = OrderTypes.Market;

		var stockBuy = CreateOrder(Sides.Buy, StockVolume);
		stockBuy.Portfolio = StockPortfolio;
		stockBuy.Security = StockSecurity;
		stockBuy.Type = OrderTypes.Market;

		return (futureSell, stockBuy);
	}

	/// <summary>
	/// Calculates weighted average price from order book for the specified volume.
	/// </summary>
	private static decimal GetAveragePrice(IOrderBookMessage depth, Sides orderDirection, decimal volume)
	{
		if (!depth.Bids.Any() || !depth.Asks.Any())
			return 0;

		var quotes = orderDirection == Sides.Buy ? depth.Asks : depth.Bids;
		var listQuotes = new List<QuoteChange>();
		decimal summVolume = 0;

		foreach (var quote in quotes)
		{
			if (summVolume >= volume)
				break;

			var diffVolume = volume - summVolume;

			if (quote.Volume <= diffVolume)
			{
				listQuotes.Add(quote);
				summVolume += quote.Volume;
			}
			else
			{
				listQuotes.Add(new QuoteChange { Price = quote.Price, Volume = diffVolume });
				summVolume += diffVolume;
			}
		}

		var totalVolume = listQuotes.Sum(s => s.Volume);
		return totalVolume > 0 ? listQuotes.Sum(s => s.Price * s.Volume) / totalVolume : 0;
	}
}