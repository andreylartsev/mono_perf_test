using Apache.NMS;
using Apache.NMS.Util;
using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static ConsoleApp1.Program.MessageReceiveTask;

namespace ConsoleApp1
{
    internal class Program
    {
        static void Main(string[] args)
        {
            try
            {
                const int MaxWaitCancellationMsecs = 10 * 60 * 1000;

                if (args.Length < 1)
                    throw new ArgumentException("Not enough arguments given");

                if (!long.TryParse(args[0], out var tasks))
                    throw new ArgumentException($"Unable to convert {args[0]} to tasks number");

                var taskArgs = args.Skip(1).ToArray();
                var cancellationTokenSource = new CancellationTokenSource();
                var cancellationToken = cancellationTokenSource.Token;

                Console.CancelKeyPress +=
                    (sender, e) =>
                    {
                        e.Cancel = true;
                        Console.WriteLine("Пользователь нажал Ctrl-C, выполняется завершения процесса...");
                        cancellationTokenSource.Cancel();
                    };

                var taskList = new List<MessageReceiveTask>();
                for (int taskId = 0; taskId < tasks; taskId++)
                {
                    var t = MakeMessageReceiveTask(taskId, taskArgs, cancellationToken);
                    taskList.Add(t);
                }

                foreach (var t in taskList)
                {
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        t.Exec();
                    });
                }

                var waitResult = WaitHandle.WaitAll(new WaitHandle [] { cancellationToken.WaitHandle }, MaxWaitCancellationMsecs);

                Console.WriteLine($"Main thread exits. waitResult={waitResult}");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Error: {e.Message}");
                Console.Error.WriteLine($"Usage: mono_perf_test1 <tasks> <protocol> <broker_uri> <user> <password> <source> <destination> <CpuUsageCyclesPerIncomingMessage> <AnswersPerIncomingMessage>");
                Environment.Exit(1);
            }
        }



        private static MessageReceiveTask MakeMessageReceiveTask(int taskId, string [] args, CancellationToken cancellationToken)
        {
            const int RequiredArguments = 8;
            MessageReceiveTask t = new MessageReceiveTask();

            if (args.Length < RequiredArguments)
                throw new ArgumentException("Not enough arguments given");

            int nextArgId = 0;
            var nextArgStr = args[nextArgId];

            if (!BrokerConnectionProtocol.TryParse(nextArgStr, out t.Protocol))
                throw new ArgumentException($"Unable to get {nameof(t.Protocol)} parameters from string {nextArgStr}");

            nextArgStr = args[++nextArgId];
            if (string.IsNullOrEmpty(nextArgStr))
                throw new ArgumentException($"{nameof(t.BrokerUri)} must not be empty");
            t.BrokerUri = nextArgStr;

            nextArgStr = args[++nextArgId];
            if (string.IsNullOrEmpty(nextArgStr))
                throw new ArgumentException($"{nameof(t.UserName)} must not be empty");
            t.UserName = nextArgStr;

            nextArgStr = args[++nextArgId];
            if (string.IsNullOrEmpty(nextArgStr))
                throw new ArgumentException($"{nameof(t.UserPassword)} must not be empty");
            t.UserPassword = nextArgStr;

            nextArgStr = args[++nextArgId];
            if (string.IsNullOrEmpty(nextArgStr))
                throw new ArgumentException($"{nameof(t.Source)} must not be empty");
            t.Source = nextArgStr;

            nextArgStr = args[++nextArgId];
            if (string.IsNullOrEmpty(nextArgStr))
                throw new ArgumentException($"{nameof(t.Destination)} must not be empty");
            t.Destination = nextArgStr;

            nextArgStr = args[++nextArgId];
            if (!int.TryParse(nextArgStr, out t.CpuUsageCyclesPerIncomingMessage))
                throw new ArgumentException($"Unable to get {nameof(t.CpuUsageCyclesPerIncomingMessage)} parameters from string {nextArgStr}");

            nextArgStr = args[++nextArgId];
            if (!int.TryParse(nextArgStr, out t.AnswersPerIncomingMessage))
                throw new ArgumentException($"Unable to get {nameof(t.AnswersPerIncomingMessage)} parameters from string {nextArgStr}");

            return t;
        }


        public enum BrokerConnectionProtocol
        {
            OpenWire,
            AMQP
        }

        public class MessageReceiveTask
        {
            public int TaskId = 0;
            public CancellationToken CancellationToken;
            public BrokerConnectionProtocol Protocol = BrokerConnectionProtocol.OpenWire;
            public string BrokerUri = string.Empty;
            public string UserName = string.Empty;
            public string UserPassword = string.Empty;
            public int CpuUsageCyclesPerIncomingMessage = 1;
            public int AnswersPerIncomingMessage = 1;
            public bool Verbose = true;
            public AcknowledgementMode SessionAcknowledgmentMode = AcknowledgementMode.Transactional;
            public MsgDeliveryMode MessageDeliveryMode = MsgDeliveryMode.Persistent;
            public string Source = string.Empty;
            public string Destination = string.Empty;
            public bool PrintMessages = false;
            public int SendRequestTimeoutInSec = 60 * 3;
            public int NoMessagesReportingTimeoutInSec = 60 * 1;

            public void Exec()
            {
                try
                {
                    if (this.Verbose)
                        PrintMessage($"Conneting to broker URI: {this.BrokerUri}");

                    var connectionFactory = MakeConnectionFactory();
                    using (var connection = connectionFactory.CreateConnection())
                    {
                        connection.Start();
                        if (this.Verbose)
                            PrintMessage($"Connected!");

                        connection.ConnectionResumedListener +=
                            () =>
                            {
                                if (this.Verbose)
                                    PrintMessage($"Lost connection has been resumed!");
                            };

                        using (var session = connection.CreateSession(this.SessionAcknowledgmentMode))
                        {
                            if (this.Verbose)
                                PrintMessage($"Created session in acknowledgment mode: {this.SessionAcknowledgmentMode}!");

                            if (this.Verbose)
                                PrintMessage($"Using source: {this.Source}");

                            IDestination source = SessionUtil.GetDestination(session, this.Source);
                            using (var consumer = session.CreateConsumer(source))
                            {

                                if (this.Verbose)
                                    PrintMessage($"And destination: {this.Destination}");

                                IDestination destination = SessionUtil.GetDestination(session, this.Destination);
                                using (IMessageProducer producer = session.CreateProducer(destination))
                                {

                                    producer.DeliveryMode = this.MessageDeliveryMode;
                                    producer.RequestTimeout = TimeSpan.FromSeconds(SendRequestTimeoutInSec);

                                    while (!this.CancellationToken.IsCancellationRequested)
                                    {

                                        var incomingMessage = consumer.Receive(TimeSpan.FromSeconds(NoMessagesReportingTimeoutInSec));
                                        if (incomingMessage != null)
                                        {
                                            var incomingText = incomingMessage.ToString();
                                            if (this.PrintMessages)
                                                PrintMessage($"--> {incomingText}!");

                                            CpuUsage(this.TaskId, this.CpuUsageCyclesPerIncomingMessage);

                                            for (int i = 0; i < this.AnswersPerIncomingMessage; i++)
                                            {
                                                var answeringMessage = session.CreateTextMessage($"Answer #{i} to {incomingText}");
                                                producer.Send(answeringMessage);
                                                if (IsTransactionalMode())
                                                    session.Commit();
                                                if (this.PrintMessages)
                                                    PrintMessage($"--> {answeringMessage.Text}!");
                                            }

                                        }
                                        else if (this.CancellationToken.IsCancellationRequested)
                                        {
                                            if (this.Verbose)
                                                PrintMessage("Received signal of program finishing!");
                                            break;
                                        }
                                        else
                                        {
                                            if (this.Verbose)
                                                PrintMessage("No new messages to answer");
                                        }
                                    }

                                }
                                if (this.Verbose)
                                    PrintMessage("Producer closed!");
                            }
                            if (this.Verbose)
                                PrintMessage("Consumer closed!");
                        }
                        if (this.Verbose)
                            PrintMessage("Session closed!");
                    }
                    if (this.Verbose)
                        PrintMessage("Connection to the broker closed!");
                }
                catch (Exception e)
                {
                    PrintMessage(e.ToString());
                }
            }
            private void PrintMessage(string message)
            {
                const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.ffffffzzz";
                var timestamp = DateTimeOffset.Now;
                Console.WriteLine($"{timestamp.ToString(TimestampFormat)} : {this.TaskId} : {message}");
            }

            private bool IsTransactionalMode()
              => this.SessionAcknowledgmentMode == AcknowledgementMode.Transactional;


            private IConnectionFactory MakeConnectionFactory()
            {
                var escapedUserName = Uri.EscapeUriString(this.UserName);
                var escapedPassword = Uri.EscapeUriString(this.UserPassword);
                var uri = this.BrokerUri.Replace("[[password]]", escapedPassword).Replace("[[username]]", escapedUserName);

                if (this.Protocol == BrokerConnectionProtocol.OpenWire)
                    return new Apache.NMS.ActiveMQ.ConnectionFactory(uri)
                    { UserName = this.UserName, Password = this.UserPassword };
                else if (this.Protocol == BrokerConnectionProtocol.AMQP)
                    return new Apache.NMS.AMQP.ConnectionFactory(uri)
                    { UserName = this.UserName, Password = this.UserPassword };
                else
                    throw new InvalidOperationException($"Protocol {this.Protocol} is unsupported!");
            }

            private static void CpuUsage(int taskId, long cycles)
            {
                long cyclesDone = 0;
                for (long i = 0; i < cycles; i++)
                {
                    const int singleCycleLength = 1_000_000;
                    long sum = 0;
                    for (long j = 0; j < singleCycleLength; j++)
                    {
                        sum += j % 5;
                    }
                    cyclesDone++;
                }
                Console.WriteLine($"taskId={taskId}, cyclesDone={cyclesDone} is done.");
            }
        }
    }
}
