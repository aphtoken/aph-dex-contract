using System;
using System.IO;
using System.Linq;
using Neo;
using Neo.VM;
using Neo.Cryptography;
using Neo.Core;

namespace APH.Contract.Tests
{
    class Program
    {
        static void Main(string[] args)
        {
            TestGetBalance();
        }

        static void TestGetBalance()
        {
            var result = RunTest("echo", new object[] { "hello world!" });
            Console.WriteLine($"Execution result {result}");
            Console.ReadLine();
        }


        static object RunTest(string operation, params object[] args)
        {
            var transaction = new InvocationTransaction();
            var engine = new ExecutionEngine(transaction, Crypto.Default);

            string contractFilePath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, @"..\..\..\..\APH.Contract\bin\Debug\APH.Contract.avm"));
            engine.LoadScript(File.ReadAllBytes(contractFilePath));

            using (ScriptBuilder sb = new ScriptBuilder())
            {
                for (int i = args.Length - 1; i >= 0; i--)
                {
                    sb.EmitPush(args[i]);
                }
                sb.EmitPush(operation);
                engine.LoadScript(sb.ToArray());
            }

            engine.Execute();

            if (engine.State.HasFlag(VMState.FAULT))
            {
                throw new Exception("FAULT");
            }

            return engine.EvaluationStack.Peek();
        }
    }
}
