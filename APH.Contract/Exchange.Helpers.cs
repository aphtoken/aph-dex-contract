using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Services.Neo;
using Neo.SmartContract.Framework.Services.System;
using System.Numerics;

namespace APH.Contract
{
    public partial class Exchange : SmartContract
    {
        /* This code only called once from Main, so has been inlined there to reduce GAS usage.
        private static byte[] GetSender()
        {
            Transaction tx = (Transaction)ExecutionEngine.ScriptContainer;

            //first check to see if the transaction has a valid sender attribute (and verify that address is also a witness)
            TransactionAttribute[] attributes = tx.GetAttributes();
            if (attributes.Length > MAX_ATTRIBUTES)
            {
                //prevent execution with too many attributes, may be a potential attack vector to cause the contract to run out of GAS
                return EMPTY;
            }

            foreach (TransactionAttribute a in attributes)
            {
                if (a.Usage == SENDER_ATTRIBUTE_USAGE)
                {
                    byte[] sender = a.Data;
                    if (Runtime.CheckWitness(sender))
                    {
                        //Runtime.Notify("GetSender() sender", sender);
                        return sender;
                    }

                    Runtime.Notify("GetSender() sender invalid, not a witness", sender);
                }
            }

            //otherwise get the sender from an input reference (they sent a system asset along with the invocation)
            TransactionOutput[] references = tx.GetReferences();
            if (references.Length > MAX_REFERENCES)
            {
                //prevent execution with too many attributes, may be a potential attack vector to cause the contract to run out of GAS
                return EMPTY;
            }

            foreach (TransactionOutput r in references)
            {
                //Runtime.Notify("GetSender() sender", references[0].ScriptHash);
                if (r.ScriptHash != ExecutionEngine.ExecutingScriptHash)
                {
                    return r.ScriptHash;
                }
            }

            Runtime.Notify("GetSender() no sender");
            return EMPTY;
        }
        */

        private static byte[] GetOwner()
        {
            byte[] owner = Storage.Get(Storage.CurrentContext, "owner");
            if (owner.Length == 0)
            {
                return DEFAULT_EXCHANGE_OWNER;
            }

            return owner;
        }

        private static byte[] GetManager()
        {
            byte[] manager = Storage.Get(Storage.CurrentContext, "manager");
            if (manager.Length == 0)
            {
                //no owner set yet, start with the owner
                manager = GetOwner();
            }

            return manager;
        }

        private static byte[] GetWhitelister()
        {
            byte[] manager = Storage.Get(Storage.CurrentContext, "whitelister");
            if (manager.Length == 0)
            {
                //no whitelister set yet, start with the manager
                manager = GetManager();
            }

            return manager;
        }

        public static bool VerifyOwner()
        {
            return Runtime.CheckWitness(GetOwner());
        }

        public static bool VerifyManager()
        {
            return Runtime.CheckWitness(GetManager());
        }

        public static bool VerifyWhitelister()
        {
            return Runtime.CheckWitness(GetWhitelister());
        }

        private static byte[] NormalizeBigIntegerTo8ByteArray(BigInteger number)
        {
            byte[] bytes = number.AsByteArray();
            return bytes.Concat(EIGHT_BYTES.Take(8 - bytes.Length));
        }

        private static byte[] NormalizeBigIntegerTo16ByteArray(BigInteger number)
        {
            byte[] bytes = number.AsByteArray();
            return bytes.Concat(SIXTEEN_BYTES.Take(16 - bytes.Length));
        }

        private static byte[] ShortToByteArray(ushort v)
        {
            byte[] bytes = ((BigInteger) v).AsByteArray();
            return bytes.Concat(TWO_BYTES.Take(2 - bytes.Length));
        }

        private static BigInteger BigIntegerFrom8Bytes(byte[] bytes)
        {
            return bytes.Length == 0 ? EIGHT_BYTES.AsBigInteger() : bytes.AsBigInteger();
        }

        private static byte[] GetUserValue(byte[] userAddress, byte[] key)
        {
            byte[] k = userAddress.Concat(key);
            return Storage.Get(Storage.CurrentContext, k);
        }

        private static void PutUserValue(byte[] userAddress, byte[] key, byte[] value)
        {
            byte[] k = userAddress.Concat(key);
            Storage.Put(Storage.CurrentContext, k, value);
        }

        private static void DeleteUserValue(byte[] userAddress, byte[] key)
        {
            byte[] k = userAddress.Concat(key);
            Storage.Delete(Storage.CurrentContext, k);
        }
    }
}
