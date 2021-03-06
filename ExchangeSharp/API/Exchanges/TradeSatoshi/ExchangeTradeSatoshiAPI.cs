﻿/*
MIT LICENSE

Copyright 2017 Digital Ruby, LLC - http://www.digitalruby.com

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using ExchangeSharp.Utility;
using Newtonsoft.Json.Linq;

namespace ExchangeSharp
{
    public sealed partial class ExchangeTradeSatoshiAPI : ExchangeAPI
    {
        public partial class ExchangeName { public const string TradeSatoshi = "TradeSatoshi"; }
        public override string BaseUrl { get; set; } = "https://tradesatoshi.com/api/";

        public ExchangeTradeSatoshiAPI()
        {
            RequestContentType = "application/json";
            NonceStyle = NonceStyle.UnixMillisecondsString;
            MarketSymbolSeparator = "_";
        }

        #region ProcessRequest 
        public string NormalizeSymbolForUrl(string symbol)
        {
            return NormalizeMarketSymbol(symbol).Replace(MarketSymbolSeparator, "_");
        }

        protected override async Task ProcessRequestAsync(IHttpWebRequest request, Dictionary<string, object> payload)
        {
            // Only Private APIs are POST and need Authorization
            if (CanMakeAuthenticatedRequest(payload) && request.Method == "POST")
            {
                var requestContentBase64String = string.Empty;
                var nonce = payload["nonce"] as string;
                payload.Remove("nonce");

                var jsonContent = payload.GetJsonForPayload();
                if (!string.IsNullOrEmpty(jsonContent))
                {
                    using (var md5 = MD5.Create())
                    {
                        requestContentBase64String = Convert.ToBase64String(md5.ComputeHash(Encoding.UTF8.GetBytes(jsonContent)));
                    }
                }

                var baseSig = string.Concat(PublicApiKey.ToUnsecureString(), request.Method, Uri.EscapeDataString(request.RequestUri.AbsoluteUri).ToLower(), nonce, requestContentBase64String);
                var signature = CryptoUtility.SHA256SignBase64(baseSig, Convert.FromBase64String(PrivateApiKey.ToUnsecureString()));
                request.AddHeader("authorization", $"amx {PublicApiKey.ToUnsecureString()}:{signature}:{nonce}");

                var content = jsonContent.ToBytesUTF8();
                await request.WriteAllAsync(content, 0, content.Length);
            }
        }

        #endregion

        #region Public APIs

        protected override async Task<IReadOnlyDictionary<string, ExchangeCurrency>> OnGetCurrenciesAsync()
        {
            var currencies = new Dictionary<string, ExchangeCurrency>();
            /*
            {
                "success": true,
                "message": null,
                "result": [
                {
                    "currency": "BOLI",
                    "currencyLong": "Bolivarcoin",
                    "minConfirmation": 3,
                    "txFee": 0.00000000,
                    "status": "OK"
                },
                {
                    "currency": "BTC",
                    "currencyLong": "Bitcoin",
                    "minConfirmation": 6,
                    "txFee": 0.00000000,
                    "status": "OK"
                }
                ]
            }*/
            var result = await MakeJsonRequestAsync<JToken>("/public/getcurrencies");
            foreach (var token in result)
            {
                var currency = new ExchangeCurrency
                {
                    Name = token["currency"].ToStringInvariant(),
                    FullName = token["currencyLong"].ToStringInvariant(),
                    MinConfirmations = token["minConfirmation"].ConvertInvariant<int>(),
                    TxFee = token["txFee"].ConvertInvariant<decimal>(),
                    DepositEnabled = token["status"].ToStringInvariant().Equals("OK"),
                    WithdrawalEnabled = token["status"].ToStringInvariant().Equals("OK")
                };
                currencies[token["currency"].ToStringInvariant()] = currency;
            }
            return currencies;
        }

        protected override async Task<IEnumerable<string>> OnGetMarketSymbolsAsync()
        {
            var result = await MakeJsonRequestAsync<JToken>("/public/getmarketsummaries");
            return result.Select(token => NormalizeSymbolForUrl(token["market"].ToStringInvariant())).ToList();
        }

        protected override async Task<IEnumerable<ExchangeMarket>> OnGetMarketSymbolsMetadataAsync()
        {
            //[{"currency":"ETHCA","active":true,"precision":8,"api_precision":8,"minimum_withdrawal_amount":"0.00200000","minimum_deposit_amount":"0.00000000","deposit_fee_currency":"ETHCA","deposit_fee_const":"0.00000000","deposit_fee_percent":0,"withdrawal_fee_currency":"ETHCA","withdrawal_fee_const":"0.00100000","withdrawal_fee_percent":0,"currency_long":"Ethcash","block_explorer_url":""}, ... ]
            var result = await MakeJsonRequestAsync<JToken>("/currencies");
            return result.Select(token => new ExchangeMarket()
            {
                MarketId = token["currency_long"].ToStringInvariant(),
                BaseCurrency = token["currency"].ToStringInvariant(),
                //MaxTradeSize = token["MaximumTrade"].ConvertInvariant<decimal>(),
                //MaxPrice = token["MaximumPrice"].ConvertInvariant<decimal>(),
                //MinTradeSize = token["MinimumTrade"].ConvertInvariant<decimal>(),
                //MinPrice = token["MinimumPrice"].ConvertInvariant<decimal>(),
                IsActive = token["active"].ToStringInvariant().Equals("true")
            })
                .ToList();
        }

        protected override async Task<ExchangeTicker> OnGetTickerAsync(string symbol)
        {
            var result = await MakeJsonRequestAsync<JToken>("/ticker");
            return ParseTicker(result, NormalizeSymbolForUrl(symbol));
        }

        protected override async Task<IEnumerable<KeyValuePair<string, ExchangeTicker>>> OnGetTickersAsync()
        {
            var result = await MakeJsonRequestAsync<JToken>("/GetMarkets");
            return result.Select(token =>
                new KeyValuePair<string, ExchangeTicker>(token["Label"].ToStringInvariant(),
                    ParseTicker(token, "TODO:PAIR"))).ToList();
        }

        protected override async Task<ExchangeOrderBook> OnGetOrderBookAsync(string symbol, int maxCount = 100)
        {
            try
            {
                var token = await MakeJsonRequestAsync<JToken>(
                    "/public/getorderbook?market=" + NormalizeSymbolForUrl(symbol));
                return ExchangeAPIExtensions.ParseOrderBookFromJTokenDictionaries(token, "sell", "buy", "rate",
                    "quantity", maxCount: maxCount);
            }
            catch (AggregateException ae)
            {
                ae.Handle(ex => true);
            }
            catch (APIException ex)
            {

            }
            return new ExchangeOrderBook();
        }

        protected override async Task<IEnumerable<ExchangeTrade>> OnGetRecentTradesAsync(string symbol)
        {
            var token = await MakeJsonRequestAsync<JToken>("/GetMarketHistory/" + NormalizeSymbolForUrl(symbol));      // Default is last 24 hours
            return token.Select(ParseTrade).ToList();
        }

        protected override async Task OnGetHistoricalTradesAsync(Func<IEnumerable<ExchangeTrade>, bool> callback, string symbol, DateTime? startDate = null, DateTime? endDate = null)
        {
            var hours = startDate == null ? "24" : ((DateTime.Now - startDate).Value.TotalHours).ToStringInvariant();
            var token = await MakeJsonRequestAsync<JToken>("/GetMarketHistory/" + NormalizeSymbolForUrl(symbol) + "/" + hours);
            var trades = token.Select(ParseTrade).ToList();
            var rc = callback?.Invoke(trades);
            // should we loop here to get additional more recent trades after a delay? 
        }


        /// <summary>
        /// Cryptopia doesn't support GetCandles. It is possible to get all trades since startdate (filter by enddate if needed) and then aggregate into MarketCandles by periodSeconds 
        /// TODO: Aggregate StocksExchange Trades into Candles
        /// </summary>
        /// <param name="symbol"></param>
        /// <param name="periodSeconds"></param>
        /// <param name="startDate"></param>
        /// <param name="endDate"></param>
        /// <param name="limit"></param>
        /// <returns></returns>
        protected override Task<IEnumerable<MarketCandle>> OnGetCandlesAsync(string symbol, int periodSeconds, DateTime? startDate = null, DateTime? endDate = null, int? limit = null)
        {
            throw new NotImplementedException();
        }

        #endregion

        #region Private APIs

        protected override async Task<Dictionary<string, decimal>> OnGetAmountsAsync()
        {
            var amounts = new Dictionary<string, decimal>();

            var payload = await GetNoncePayloadAsync();
            payload.Add("Currency", "");

            var token = await MakeJsonRequestAsync<JToken>("/GetBalance", null, payload, "POST");
            if (!token.HasValues) return amounts;
            foreach (var currency in token)
            {
                var amount = currency["Total"].ConvertInvariant<decimal>();
                if (amount > 0) amounts.Add(currency["Symbol"].ToStringInvariant(), amount);
            }
            return amounts;
        }

        protected override async Task<Dictionary<string, decimal>> OnGetAmountsAvailableToTradeAsync()
        {
            var amounts = new Dictionary<string, decimal>();

            var payload = await GetNoncePayloadAsync();
            payload.Add("Currency", "");

            // [ { "CurrencyId":1,"Symbol":"BTC","Total":"10300","Available":"6700.00000000","Unconfirmed":"2.00000000","HeldForTrades":"3400,00000000","PendingWithdraw":"200.00000000", "Address":"4HMjBARzTNdUpXCYkZDTHq8vmJQkdxXyFg","BaseAddress": "ZDTHq8vmJQkdxXyFgZDTHq8vmJQkdxXyFgZDTHq8vmJQkdxXyFg","Status":"OK", "StatusMessage":"" }, ... ]
            var token = await MakeJsonRequestAsync<JToken>("/GetBalance", null, payload, "POST");
            if (!token.HasValues) return amounts;
            foreach (var currency in token)
            {
                var amount = currency["Available"].ConvertInvariant<decimal>();
                if (amount > 0) amounts.Add(currency["Symbol"].ToStringInvariant(), amount);
            }
            return amounts;
        }

        protected override async Task<IEnumerable<ExchangeOrderResult>> OnGetCompletedOrderDetailsAsync(string symbol = null, DateTime? afterDate = null)
        {
            var payload = await GetNoncePayloadAsync();
            if (!string.IsNullOrEmpty(symbol)) payload["Market"] = symbol;
            else payload["Market"] = string.Empty;

            // [ { "TradeId": 23467, "TradePairId": 100,"Market": "DOT/BTC","Type": "Buy","Rate": 0.00000034, "Amount": 145.98000000, "Total": "0.00004963", "Fee": "0.98760000", "TimeStamp":"2014-12-07T20:04:05.3947572" }, ... ]
            var token = await MakeJsonRequestAsync<JToken>("/GetTradeHistory", null, payload, "POST");
            return token.Select(order => new ExchangeOrderResult()
            {
                OrderId = order["TradeId"].ConvertInvariant<int>().ToStringInvariant(),
                MarketSymbol = order["Market"].ToStringInvariant(),
                Amount = order["Amount"].ConvertInvariant<decimal>(),
                AmountFilled = order["Amount"].ConvertInvariant<decimal>(), // It doesn't look like partial fills are supplied on closed orders
                Price = order["Rate"].ConvertInvariant<decimal>(),
                AveragePrice = order["Rate"].ConvertInvariant<decimal>(),
                OrderDate = order["TimeStamp"].ToDateTimeInvariant(),
                IsBuy = order["Type"].ToStringInvariant().Equals("Buy"),
                Fees = order["Fee"].ConvertInvariant<decimal>(),
                Result = ExchangeAPIOrderResult.Filled
            })
                .ToList();
        }

        protected override async Task<IEnumerable<ExchangeOrderResult>> OnGetOpenOrderDetailsAsync(string symbol = null)
        {
            var orders = new List<ExchangeOrderResult>();

            var payload = await GetNoncePayloadAsync();
            payload["Market"] = string.IsNullOrEmpty(symbol) ? string.Empty : symbol;

            //[ {"OrderId": 23467,"TradePairId": 100,"Market": "DOT/BTC","Type": "Buy","Rate": 0.00000034,"Amount": 145.98000000, "Total": "0.00004963", "Remaining": "23.98760000", "TimeStamp":"2014-12-07T20:04:05.3947572" }, ... ]
            var token = await MakeJsonRequestAsync<JToken>("/GetOpenOrders", null, payload, "POST");
            foreach (var data in token)
            {
                var order = new ExchangeOrderResult()
                {
                    OrderId = data["OrderId"].ConvertInvariant<int>().ToStringInvariant(),
                    OrderDate = data["TimeStamp"].ToDateTimeInvariant(),
                    MarketSymbol = data["Market"].ToStringInvariant(),
                    Amount = data["Amount"].ConvertInvariant<decimal>(),
                    Price = data["Rate"].ConvertInvariant<decimal>(),
                    IsBuy = data["Type"].ToStringInvariant() == "Buy"
                };
                order.AveragePrice = order.Price;
                order.AmountFilled = order.Amount - data["Remaining"].ConvertInvariant<decimal>();
                if (order.AmountFilled == 0m) order.Result = ExchangeAPIOrderResult.Pending;
                else if (order.AmountFilled < order.Amount) order.Result = ExchangeAPIOrderResult.FilledPartially;
                else if (order.Amount == order.AmountFilled) order.Result = ExchangeAPIOrderResult.Filled;
                else order.Result = ExchangeAPIOrderResult.Unknown;

                orders.Add(order);
            }
            return orders;
        }


        /// <summary>
        /// Not directly supported by Cryptopia, and this API call is ambiguous between open and closed orders, so we'll get all Closed orders and filter for OrderId
        /// </summary>
        /// <param name="orderId"></param>
        /// <param name="symbol"></param>
        /// <returns></returns>
        protected override async Task<ExchangeOrderResult> OnGetOrderDetailsAsync(string orderId, string symbol = null)
        {
            var orders = await GetCompletedOrderDetailsAsync();
            return orders.FirstOrDefault(o => o.OrderId == orderId);
        }

        protected override async Task<ExchangeOrderResult> OnPlaceOrderAsync(ExchangeOrderRequest order)
        {
            var newOrder = new ExchangeOrderResult() { Result = ExchangeAPIOrderResult.Error };

            var payload = await GetNoncePayloadAsync();
            payload["Market"] = order.MarketSymbol;
            payload["Type"] = order.IsBuy ? "Buy" : "Sell";
            payload["Rate"] = order.Price;
            payload["Amount"] = order.Amount;
            order.ExtraParameters.CopyTo(payload);

            var token = await MakeJsonRequestAsync<JToken>("/SubmitTrade", null, payload, "POST");
            if (!token.HasValues || token["OrderId"] == null) return newOrder;
            newOrder.OrderId = token["OrderId"].ConvertInvariant<int>().ToStringInvariant();
            newOrder.Result = ExchangeAPIOrderResult.Pending;           // Might we change this depending on what the filled orders are?
            return newOrder;
        }

        // This should have a return value for success
        protected override async Task OnCancelOrderAsync(string orderId, string symbol = null)
        {
            var payload = await GetNoncePayloadAsync();
            payload["Type"] = "Trade";          // Cancel All by Market is supported. Here we're canceling by single Id
            payload["OrderId"] = int.Parse(orderId);
            // { "Success":true, "Error":null, "Data": [44310,44311]  }
            await MakeJsonRequestAsync<JToken>("/CancelTrade", null, payload, "POST");
        }

        /// <summary>
        /// Cryptopia does support filtering by Transaction Type (deposits and withdraws), but here we're returning both. The Tx Type will be returned in the Message field
        /// By Symbol isn't supported, so we'll filter. Also, the default limit is 100 transactions, we could possibly increase this to support the extra data we have to return for Symbol
        /// </summary>
        /// <param name="symbol"></param>
        /// <returns></returns>
        protected override async Task<IEnumerable<ExchangeTransaction>> OnGetDepositHistoryAsync(string symbol)
        {
            var deposits = new List<ExchangeTransaction>();
            var payload = await GetNoncePayloadAsync();
            // Uncomment as desired
            //payload["Type"] = "Deposit";
            //payload["Type"] = "Withdraw";
            //payload["Count"] = 100;

            var token = await MakeJsonRequestAsync<JToken>("/GetTransactions", null, payload, "POST");
            foreach (var data in token)
            {
                if (data["Currency"].ToStringInvariant().Equals(symbol))
                {
                    var tx = new ExchangeTransaction()
                    {
                        Address = data["Address"].ToStringInvariant(),
                        Amount = data["Amount"].ConvertInvariant<decimal>(),
                        BlockchainTxId = data["TxId"].ToStringInvariant(),
                        Notes = data["Type"].ToStringInvariant(),
                        PaymentId = data["Id"].ToStringInvariant(),
                        Timestamp = data["TimeStamp"].ToDateTimeInvariant(),
                        Currency = data["Currency"].ToStringInvariant(),
                        TxFee = data["Fee"].ConvertInvariant<decimal>()
                    };
                    // They may support more status types, but it's not documented
                    switch ((string)data["Status"])
                    {
                        case "Confirmed": tx.Status = TransactionStatus.Complete; break;
                        case "Pending": tx.Status = TransactionStatus.Processing; break;
                        default: tx.Status = TransactionStatus.Unknown; break;
                    }
                    deposits.Add(tx);
                }
            }
            return deposits;
        }

        protected override async Task<ExchangeDepositDetails> OnGetDepositAddressAsync(string symbol, bool forceRegenerate = false)
        {

            var payload = await GetNoncePayloadAsync();
            payload["Currency"] = symbol;
            var token = await MakeJsonRequestAsync<JToken>("/GetDepositAddress", null, payload, "POST");
            if (token["Address"] == null) return null;
            return new ExchangeDepositDetails()
            {
                Currency = symbol,
                Address = token["Address"].ToStringInvariant(),
                AddressTag = token["BaseAddress"].ToStringInvariant()
            };
        }

        protected override async Task<ExchangeWithdrawalResponse> OnWithdrawAsync(ExchangeWithdrawalRequest withdrawalRequest)
        {
            var response = new ExchangeWithdrawalResponse { Success = false };

            var payload = await GetNoncePayloadAsync();
            payload.Add("Currency", withdrawalRequest.Currency);
            payload.Add("Address", withdrawalRequest.Address);
            if (!string.IsNullOrEmpty(withdrawalRequest.AddressTag)) payload.Add("PaymentId", withdrawalRequest.AddressTag);
            payload.Add("Amount", withdrawalRequest.Amount);
            var token = await MakeJsonRequestAsync<JToken>("/SubmitWithdraw", null, payload, "POST");
            response.Id = token.ConvertInvariant<int>().ToStringInvariant();
            response.Success = true;
            return response;
        }


        #endregion

        #region Private Functions

        private static ExchangeTicker ParseTicker(JToken token, string symbol)
        {
            //{ "min_order_amount":"0.00000010","ask":"0.00001600","bid":"0.00001357","last":"0.00001441","lastDayAgo":"0.00001261","vol":"283.00389206","spread":"0","buy_fee_percent":"0.2","sell_fee_percent":"0.2","market_name":"PRG_BTC","market_id":35,"updated_time":1531385896,"server_time":1531385896}
            if (!token.Any()) return null;
            var symbols = symbol.Split('_');
            foreach (var tokenTicker in token)
            {
                if (tokenTicker["market_name"].ToStringInvariant() == symbol)
                {
                    var ticker = new ExchangeTicker()
                    {
                        Id = tokenTicker["market_id"].ToStringInvariant(),
                        Ask = tokenTicker["ask"].ConvertInvariant<decimal>(),
                        Bid = tokenTicker["bid"].ConvertInvariant<decimal>(),
                        Last = tokenTicker["last"].ConvertInvariant<decimal>(),
                        // Since we're parsing a ticker for a market, we'll use the volume/baseVolume fields here and ignore the Buy/Sell Volumes
                        // This is a guess as to ambiguous intent of these fields.
                        Volume = new ExchangeVolume()
                        {
                            BaseCurrency = symbols[0],
                            QuoteCurrency = symbols[1],
                            BaseCurrencyVolume =  tokenTicker["vol"].ConvertInvariant<decimal>(),
                            Timestamp = DateTimeExtensions.UnixTimeStampToDateTime(tokenTicker["updated_time"].ConvertInvariant<double>())
                        }
                    };
                    ticker.Volume.QuoteCurrencyVolume = ticker.Volume.BaseCurrencyVolume / ticker.Last;
                    return ticker;
                }
            }
            return null;
        }

        private static ExchangeTrade ParseTrade(JToken token)
        {
            //ExchangeTrade trade = new ExchangeTrade()
            //{
            //    Timestamp = DateTimeOffset.FromUnixTimeSeconds(token["Timestamp"].ConvertInvariant<long>()).DateTime,
            //    Amount = token["Amount"].ConvertInvariant<decimal>(),
            //    Price = token["Price"].ConvertInvariant<decimal>(),
            //    IsBuy = token["Type"].ToStringInvariant().Equals("Buy")
            //};
            //return trade;
            throw new NotImplementedException();
        }

        #endregion

    }
}
