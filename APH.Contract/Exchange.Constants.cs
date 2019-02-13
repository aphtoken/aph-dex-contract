using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Services.Neo;
using Neo.SmartContract.Framework.Services.System;
using System.Numerics;

namespace APH.Contract
{
    public partial class Exchange : SmartContract
    {
        private static readonly byte[] NEO = { 155, 124, 255, 218, 166, 116, 190, 174, 15, 147, 14, 190, 96, 133, 175, 144, 147, 229, 254, 86, 179, 74, 92, 34, 12, 205, 207, 110, 252, 51, 111, 197 };
        private static readonly byte[] GAS = { 231, 45, 40, 105, 121, 238, 108, 177, 183, 230, 93, 253, 223, 178, 227, 132, 16, 11, 141, 20, 142, 119, 88, 222, 66, 228, 22, 139, 113, 121, 44, 96 };
        // mainnet:
        //private static readonly byte[] APH = "AVNeKnqNa8zHRMysAiMaBcPVT51T6p3MVi".ToScriptHash();

        // testnet:
        //private static readonly byte[] APH = { 0x4a, 0x90, 0x91, 0x13, 0x7e, 0x20, 0x26, 0xef, 0x04, 0xfe, 0xee, 0xed, 0x81, 0x89, 0x9a, 0x37, 0xcd, 0xed, 0x1e, 0x59 };
        //private static readonly byte[] APH = "ANa8qN6FLgeFXoiVjtVDvTBRtzmVeFxT9D".ToScriptHash(); // testnet APH scripthash
        // jeff privnet:
        //private static readonly byte[] APH = { 0x61, 0x10, 0xd1, 0x69, 0xe2, 0x74, 0xa9, 0xd4, 0xb0, 0xbf, 0x3c, 0x31, 0xfb, 0x71, 0xb1, 0x61, 0xa2, 0xd8, 0x8a, 0xfe };
        private static readonly byte[] APH = "AVNeKnqNa8zHRMysAiMaBcPVT51T6p3MVi".ToScriptHash();

        private static readonly byte[] DEFAULT_EXCHANGE_OWNER = "AQxzxZbp8xN5Go4ho5SxMcqDo94bjqvdUm".ToScriptHash();

            // 1693d806f67bdd67f9b2f347aabc6ef4a9f81a1a
        private static readonly ushort DEFAULT_FEE_REDISTRIBUTION_PERCENTAGE = 80;
        private static readonly ushort DEFAULT_CLAIM_MINIMUM_BLOCKS = 4800;

        public static readonly byte[] PREFIX_OFFERS = { 0x0F, 0xFE, 0x75, 0x0F, 0xFE, 0x75 };
        public static readonly byte[] PREFIX_ASSET_SETTINGS = { 0x5E, 0x77, 0x12, 0x95 };
        public static readonly byte[] PREFIX_WHITELIST = "WL".AsByteArray();
        public static readonly byte[] PREFIX_USERIDENTITY = "UI".AsByteArray();
        public static readonly byte[] PREFIX_MARKETS = "markets".AsByteArray();
        public static readonly byte[] PREFIX_BASE_CONVERSION_RATE = "baserate".AsByteArray();

        public static readonly byte[] POSTFIX_USER_ASSET_WITHDRAWING = { 0xB0 };
        public static readonly byte[] POSTFIX_ASSET_USER_BALANCES = { 0xBA };
        public static readonly byte[] POSTFIX_USER_ASSET_CONTRIBUTED = { 0xD0 };

        public static readonly byte[] USER_ASSET_TYPE_NEP5_EXTENSIONS = { 0x01 };

        // public static readonly byte[] POSTFIX_FEES_COLLECTED = { 0xFC };
        // public static readonly byte[] POSTFIX_CONTRIBUTIONS = { 0xFA };
        // public static byte[] FEES_POOL_KEY => APH.Concat(POSTFIX_FEES_COLLECTED);
        // public static byte[] CURRENT_CONTRIBUTIONS_KEY => APH.Concat(POSTFIX_CONTRIBUTIONS);
        // ^ The above 4 lines require an extra unneecessary call; avoiding that.
        // jeff privnet:
        public static readonly byte[] FEES_POOL_KEY = { 0x61, 0x10, 0xd1, 0x69, 0xe2, 0x74, 0xa9, 0xd4, 0xb0, 0xbf, 0x3c, 0x31, 0xfb, 0x71, 0xb1, 0x61, 0xa2, 0xd8, 0x8a, 0xfe, 0xfc };
        public static readonly byte[] CURRENT_CONTRIBUTIONS_KEY = { 0x61, 0x10, 0xd1, 0x69, 0xe2, 0x74, 0xa9, 0xd4, 0xb0, 0xbf, 0x3c, 0x31, 0xfb, 0x71, 0xb1, 0x61, 0xa2, 0xd8, 0x8a, 0xfe, 0xfa };
        // testnet:
        //public static readonly byte[] FEES_POOL_KEY = { 0x4a, 0x90, 0x91, 0x13, 0x7e, 0x20, 0x26, 0xef, 0x04, 0xfe, 0xee, 0xed, 0x81, 0x89, 0x9a, 0x37, 0xcd, 0xed, 0x1e, 0x59, 0xfc };
        //public static readonly byte[] CURRENT_CONTRIBUTIONS_KEY = { 0x4a, 0x90, 0x91, 0x13, 0x7e, 0x20, 0x26, 0xef, 0x04, 0xfe, 0xee, 0xed, 0x81, 0x89, 0x9a, 0x37, 0xcd, 0xed, 0x1e, 0x59, 0xfa };

        // mainnet:
        //public static readonly byte[] FEES_POOL_KEY = { 0x95, 0x2d, 0x12, 0xa0, 0x25, 0x32, 0x5e, 0x56, 0xa4, 0xcb, 0x3b, 0xa2, 0xd4, 0x69, 0xb1, 0xe2, 0x3c, 0x7c, 0x77, 0xa0, 0xfc };
        //public static readonly byte[] CURRENT_CONTRIBUTIONS_KEY = { 0x95, 0x2d, 0x12, 0xa0, 0x25, 0x32, 0x5e, 0x56, 0xa4, 0xcb, 0x3b, 0xa2, 0xd4, 0x69, 0xb1, 0xe2, 0x3c, 0x7c, 0x77, 0xa0, 0xfa };

        public static readonly byte[] EMPTY = { };
        public static readonly byte[] TWO_BYTES = { 0, 0 };
        public static readonly byte[] EIGHT_BYTES = { 0, 0, 0, 0, 0, 0, 0, 0 };
        public static readonly byte[] SIXTEEN_BYTES = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        public static readonly BigInteger ONE = 100000000;
        public static readonly int MAX_ATTRIBUTES = 25;
        public static readonly int MAX_REFERENCES = 100;
        public static readonly byte SENDER_ATTRIBUTE_USAGE = 0x20;
    }
}
