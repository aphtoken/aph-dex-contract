using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Services.Neo;
using System.Numerics;

namespace APH.Contract
{
    public partial class Exchange : SmartContract
    {
        public class Market
        {
            public byte[] QuoteAssetId;
            public byte[] BaseAssetId;
            public BigInteger MinimumSize;
            public BigInteger MinimumTickSize;
            public BigInteger BuyFeePercent; // has 6 decimals: ##.######, so value is percentage * 100000000
            public BigInteger SellFeePercent;
        }

        private static bool SetMarket(object[] args)
        {
            string failReason;
            if (args.Length != 6)
            {
                // Runtime.Notify(setMarketFailStr, "requires six parameters", args.Length);
                return false;
            }

            var quoteAssetId = (byte[])args[0];
            var baseAssetId = (byte[])args[1];
            if (quoteAssetId.Length != 32 && quoteAssetId.Length != 20 ||
                baseAssetId.Length != 32 && baseAssetId.Length != 20)
            {
                failReason = "bad asset len";
                goto SetMarketFail;
            }

            byte[] marketId = PREFIX_MARKETS.Concat(quoteAssetId).Concat(baseAssetId);

            Market market;
            if (VerifyManager() == false && VerifyOwner() == false)
            {
                byte[] existingMarket = Storage.Get(Storage.CurrentContext, marketId);
                if (existingMarket.Length == 0 || VerifyWhitelister() == false)
                {
                    failReason = "No permission";
                    goto SetMarketFail;
                }
                // Only make it here if the whitelister is calling with an already existing market.
                // We allow programatically changing the minimumSize, buyFee, and sellFee by the whitelister.
                market = (Market) existingMarket.Deserialize();
                market.MinimumSize = (BigInteger)args[2];
                market.BuyFeePercent = (BigInteger)args[4];
                market.SellFeePercent = (BigInteger)args[5];
            }
            else
            {
                market = new Market();
                market.QuoteAssetId = quoteAssetId;
                market.BaseAssetId = baseAssetId;
                market.MinimumSize = (BigInteger)args[2];
                market.MinimumTickSize = (BigInteger)args[3];
                market.BuyFeePercent = (BigInteger)args[4];
                market.SellFeePercent = (BigInteger)args[5];
            }

            if (market.BuyFeePercent < 0 || market.SellFeePercent < 0)
            {
                failReason = "Fee cannot be negative";
                goto SetMarketFail;
            }

            // Verify base asset and quote asset have asset settings set
            byte[] quoteAssetSettings = Storage.Get(Storage.CurrentContext, PREFIX_ASSET_SETTINGS.Concat(market.QuoteAssetId));
            byte[] baseAssetSettings = Storage.Get(Storage.CurrentContext, PREFIX_ASSET_SETTINGS.Concat(market.BaseAssetId));
            if (quoteAssetSettings.Length == 0 || baseAssetSettings.Length == 0)
            {
                failReason = "Invalid Asset";
                goto SetMarketFail;
            }

            byte[] marketData = market.Serialize();
            Storage.Put(Storage.CurrentContext, marketId, marketData);
            Runtime.Notify("putMarket", marketId, marketData);
            Runtime.Notify("marketSet", marketId, market.QuoteAssetId, market.BaseAssetId, market.MinimumSize,
                market.MinimumTickSize, market.BuyFeePercent, market.SellFeePercent);
            return true;
SetMarketFail:
            Runtime.Notify("setMarketFail", failReason);
            return false;
        }

        private static bool CloseMarket(object[] args)
        {
            string failReason;
            if (args.Length != 2)
            {
                failReason = "Requires 2 params";
                goto CloseMarketFail;
            }

            if (VerifyManager() == false && VerifyOwner() == false)
            {
                failReason = "No permission";
                goto CloseMarketFail;
            }

            byte[] quoteAssetId = (byte[])args[0];
            byte[] baseAssetId = (byte[])args[1];

            if (quoteAssetId.Length != 32 && quoteAssetId.Length != 20 ||
                baseAssetId.Length != 32 && baseAssetId.Length != 20)
            {
                failReason = "Invalid length of base or quote asset";
                goto CloseMarketFail;
            }

            byte[] marketId = PREFIX_MARKETS.Concat(quoteAssetId).Concat(baseAssetId);
            Storage.Delete(Storage.CurrentContext, marketId);
            Runtime.Notify("marketClosed", quoteAssetId, baseAssetId);
            return true;
CloseMarketFail:
            Runtime.Notify("closeMarketFail", failReason);
            return false;
        }

        private static Market GetMarket(byte[] assetId1, byte[] assetId2)
        {
            StorageContext storageContext = Storage.CurrentContext;
            byte[] marketId = PREFIX_MARKETS.Concat(assetId1).Concat(assetId2);
            byte[] marketData = Storage.Get(storageContext, marketId);

            if (marketData.Length != 0)
            {
                return (Market) marketData.Deserialize();
            }

            //not found, check the opposite order
            marketId = PREFIX_MARKETS.Concat(assetId2).Concat(assetId1);
            marketData = Storage.Get(storageContext, marketId);
            if (marketData.Length != 0)
            {
                return (Market) marketData.Deserialize();
            }

            Market blank = new Market();
            blank.BaseAssetId = EMPTY;
            blank.QuoteAssetId = EMPTY;
            return blank;
        }

        /// <summary>
        /// Set the conversion rate between base asset and APH.
        /// </summary>
        /// <param name="baseAssetId">the base asset</param>
        /// <param name="conversionRate">the amount (fixed8) of APH for 1 unit of base asset</param>
        /// <returns>true if successfully set, otherwise false.</returns>
        private static bool SetAssetToAphConversionRate(byte[] baseAssetId, BigInteger conversionRate)
        {
            if (baseAssetId.Length != 32 && baseAssetId.Length != 20)
                return false;
            if (VerifyWhitelister() == false && VerifyManager() == false && VerifyOwner() == false)
                return false;
            Storage.Put(Storage.CurrentContext, PREFIX_BASE_CONVERSION_RATE.Concat(baseAssetId), conversionRate);
            return true;
        }

        public static BigInteger GetAssetToAphConversionRate(byte[] baseAssetId)
        {
            return Storage.Get(Storage.CurrentContext, PREFIX_BASE_CONVERSION_RATE.Concat(baseAssetId)).AsBigInteger();
        }
    }
}
