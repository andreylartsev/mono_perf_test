using Apache.NMS;
using Apache.NMS.ActiveMQ.Commands;
using Apache.NMS.Util;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace ConsoleApp1
{
    [Cli.Doc(@"[[program]] recived message from source queue, doing some CPU-intensive usage operations replays with some outgoing messages to destination queue")]
    [Cli.GenerateSample]
    internal class Program
    {
        [Cli.Named]
        [Cli.SampleValue("8")]
        [Cli.AllowedRange(1, 128)]
        public int Tasks = 8;

        [Cli.Named]
        [Cli.SampleValue("OpenWire")]
        public BrokerConnectionProtocol Protocol = BrokerConnectionProtocol.OpenWire;

        [Cli.Named]
        [Cli.SampleValue("ssl://10.228.4.104:61617?transport.acceptInvalidBrokerCert=true")]
        public string BrokerUri = string.Empty;

        [Cli.Named]
        [Cli.SampleValue("main")]
        public string UserName = string.Empty;

        [Cli.Named]
        [Cli.Secret]
        [Cli.EnvironmentVariable("USER_PASSWORD")] 
        public string UserPassword = string.Empty;

        [Cli.Named]
        [Cli.AllowedRange(0, 100_000)]
        public int CpuUsageCyclesPerIncomingMessage = 1;

        [Cli.Named]
        [Cli.SampleValue("3")]
        [Cli.AllowedRange(0, 1000)]
        public int AnswersPerIncomingMessage = 1;

        [Cli.Named]
        [Cli.SampleValue("TO.KM.LAGS.TEST")]
        public string Source = string.Empty;

        [Cli.Named]
        [Cli.SampleValue("TO.KM.LAGS.TEST2")]
        public string Destination = string.Empty;

        [Cli.Named]
        public bool PrintMessages = true;

        [Cli.Named]
        [Cli.SampleValue("ClientAcknowledge")]
        public AcknowledgementMode SessionAcknowledgmentMode = AcknowledgementMode.ClientAcknowledge;

        [Cli.Named]
        public bool Verbose = true;

        [Cli.Named]
        [Cli.SampleValue("Persistent")]
        public MsgDeliveryMode MessageDeliveryMode = MsgDeliveryMode.Persistent;

        [Cli.Named]
        [Cli.SampleValue("180")]
        [Cli.AllowedRange(1, 6000)]
        public int SendRequestTimeoutInSec = 60 * 3;

        [Cli.Named]
        [Cli.SampleValue("10")]
        [Cli.AllowedRange(0, 6000)]
        public int NoMessagesReportingTimeoutInSec = 5 * 1;


        [Cli.Named]
        [Cli.AllowedRange(1, 86400)]
        public int MaxWaitCancellationInSec = 30 * 60;

        [Cli.Named]
        [Cli.AllowedRange(0, 180*1000)]
        public int RandomFactorForConnectionStartInMsecs = 100;

        [Cli.Named]
        [Cli.AllowedRange(0, 180 * 1000)]
        public int RandomFactorForMessageReceiveInMsecs = 100;

        [Cli.Named]
        [Cli.AllowedRange(0, 10_000)]
        public int RandomFactorForCpuUsageInCycles = 0;

        public static void Main(string[] args)
        {
            Program program = new Program();
            try
            {
                Cli.ParseCommandLine(args, program);
                program.Exec();
            }
            catch (Cli.PrintVersionException)
            {
                Cli.PrintVersion();
            }
            catch (Cli.PrintAppSettingsException)
            {
                Cli.PrintAppSettings(program);
            }
            catch (Cli.ProgramHelpException e)
            {
                Cli.PrintCommandLine(args);
                Cli.PrintUsage(program, e.HelpType);
            }
            catch (Cli.CommandHelpException e)
            {
                Cli.PrintCommandUsage(e.Command, e.HelpType);
            }
            catch (Cli.UnknownCommandException e)
            {
                Console.WriteLine(e.Message);
                Cli.PrintCommandLine(args);
                Cli.PrintUsage(program);
            }
            catch (Cli.ArgumentParseException e)
            {
                Console.WriteLine(e.Message);
                Cli.PrintCommandLine(args);
                Cli.PrintCommandUsage(e.Command as Cli.ICommand);
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
                Console.WriteLine(e.StackTrace);
            }
        }

        public void Exec()
        {
            var cancellationTokenSource = new CancellationTokenSource();

            Console.CancelKeyPress +=
                (sender, e) =>
                {
                    e.Cancel = true;
                    Console.WriteLine("User pressed Ctrl-C, finishing process...");
                    cancellationTokenSource.Cancel();
                };

            var allWaitHandlesList = new List<WaitHandle>();
            var cancellationToken = cancellationTokenSource.Token;
            allWaitHandlesList.Add(cancellationToken.WaitHandle);

            var taskList = new List<MessageReceiveTask>();

            for (int taskId = 0; taskId < this.Tasks; taskId++)
            {
                var t = MakeMessageReceiveTask(taskId, cancellationToken);
                taskList.Add(t);
                allWaitHandlesList.Add(t.EndEventHandle);
            }

            foreach (var t in taskList)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    t.Exec();
                });
            }

            var allWaitHandlesArray = allWaitHandlesList.ToArray();
            var waitResult = WaitHandle.WaitAll(allWaitHandlesArray, this.MaxWaitCancellationInSec * 1000);

            foreach (var task in taskList)
            {
                task.PrintStats();
            }

            Console.WriteLine($"Main thread exits. waitResult={waitResult}");

        }

        private MessageReceiveTask MakeMessageReceiveTask(int taskId, CancellationToken cancellationToken)
        {
            MessageReceiveTask t = new MessageReceiveTask();
            t.TaskId = taskId;
            t.CancellationToken = cancellationToken;
            t.Protocol = this.Protocol;
            t.BrokerUri = this.BrokerUri;
            t.UserName = this.UserName;
            t.UserPassword = this.UserPassword;
            t.Source = this.Source;
            t.Destination = this.Destination;
            t.CpuUsageCyclesPerIncomingMessage = this.CpuUsageCyclesPerIncomingMessage;
            t.AnswersPerIncomingMessage = this.AnswersPerIncomingMessage;
            t.MessageDeliveryMode = this.MessageDeliveryMode;
            t.SessionAcknowledgmentMode = this.SessionAcknowledgmentMode;
            t.PrintMessages = this.PrintMessages;
            t.Verbose = this.Verbose;
            t.NoMessagesReportingTimeoutInSec = this.NoMessagesReportingTimeoutInSec;
            t.SendRequestTimeoutInSec = this.SendRequestTimeoutInSec;   
            t.RandomFactorForConnectionStartInMsecs = this.RandomFactorForConnectionStartInMsecs;
            t.RandomFactorForMessageReceiveInMsecs = this.RandomFactorForMessageReceiveInMsecs;
            t.RandomFactorForCpuUsageInCycles = this.RandomFactorForCpuUsageInCycles;

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
            public string Source = string.Empty;
            public string Destination = string.Empty;
            public bool PrintMessages = true;
            public AcknowledgementMode SessionAcknowledgmentMode = AcknowledgementMode.ClientAcknowledge;
            public bool Verbose = true;
            public MsgDeliveryMode MessageDeliveryMode = MsgDeliveryMode.Persistent;
            public int SendRequestTimeoutInSec = 60 * 3;
            public int NoMessagesReportingTimeoutInSec = 5 * 1;
            public int RandomFactorForConnectionStartInMsecs = 1;
            public int RandomFactorForMessageReceiveInMsecs = 1;
            public int RandomFactorForCpuUsageInCycles = 1;
            public ManualResetEvent EndEventHandle = new ManualResetEvent(false);
            public ProcessingStats AverageStats = new ProcessingStats();

            public void Exec()
            {
                try
                {
                    var random = new Random();
                    try
                    {
                        if (this.RandomFactorForConnectionStartInMsecs > 0)
                        {
                            var waitFor = random.Next(RandomFactorForConnectionStartInMsecs);
                            if (this.Verbose)
                                PrintMessage($"Randomized wait before connection start for {waitFor} msecs");
                            Thread.Sleep(waitFor);
                        }

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
                                            var stats = new ProcessingStats();
                                            var stopWatch = Stopwatch.StartNew();

                                            if (this.RandomFactorForMessageReceiveInMsecs > 0)
                                            {
                                                var waitFor = random.Next(this.RandomFactorForMessageReceiveInMsecs);
                                                if (this.Verbose)
                                                    PrintMessage($"Random pause for getting new message for {waitFor} msecs");
                                                Thread.Sleep(waitFor);
                                            }

                                            if (this.Verbose)
                                                PrintMessage($"Wait before new message for {this.NoMessagesReportingTimeoutInSec} secs");

                                            stats.SleptBeforeReceiveMessageTicks = stopWatch.ElapsedTicks;
                                            
                                            stopWatch.Restart();                                            
                                            var incomingMessage = consumer.Receive(TimeSpan.FromSeconds(NoMessagesReportingTimeoutInSec));
                                            stats.TookToReceiveMessageTicks = stopWatch.ElapsedTicks;

                                            if (this.CancellationToken.IsCancellationRequested)
                                            {
                                                if (this.Verbose)
                                                    PrintMessage("Cancellation has is requested. Finising processing");
                                                if (incomingMessage != null)
                                                    incomingMessage.Acknowledge();
                                                break;
                                            }

                                            if (incomingMessage != null)
                                            {
                                                ProcessMessage(random, stats, session, producer, incomingMessage);
                                                UpdateAverageStats(this.AverageStats, stats);
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
                finally 
                {
                    EndEventHandle.Set();
                }
            }

            private void ProcessMessage(Random random, ProcessingStats stats, ISession session, IMessageProducer producer, IMessage incomingMessage)
            {
                var stopWatch = Stopwatch.StartNew();

                var incomingText = (incomingMessage is ITextMessage) ? (incomingMessage as ITextMessage).Text : "non-text messaage";
                if (this.PrintMessages)
                    PrintMessage($"<-- {incomingText}!");

                var cpuUsageCycles = this.CpuUsageCyclesPerIncomingMessage;

                if (RandomFactorForCpuUsageInCycles > 0)
                {
                    var extraCycles = random.Next(this.RandomFactorForCpuUsageInCycles);
                    cpuUsageCycles += extraCycles;
                }

                stopWatch.Restart();
                CpuUsage(this.TaskId, cpuUsageCycles);
                stats.TookToProcessMessageTicks = stopWatch.ElapsedTicks;

                stopWatch.Restart();
                for (int i = 0; i < this.AnswersPerIncomingMessage; i++)
                {
                    var answeringMessage = session.CreateTextMessage($"Answer #{i} to {incomingText}");
                    producer.Send(answeringMessage);
                    if (this.PrintMessages)
                        PrintMessage($"--> {answeringMessage.Text}!");
                }
                stats.TookToAnswerMessageTicks = stopWatch.ElapsedTicks;

                stopWatch.Restart();
                incomingMessage.Acknowledge();
                stats.TookToAcknowledgeMessageTicks = stopWatch.ElapsedTicks;

                stopWatch.Restart();
                if (IsTransactionalMode())
                    session.Commit();
                stats.TookToCommitMessageProcessingTicks = stopWatch.ElapsedTicks;

            }

            public class ProcessingStats
            {
                public long SleptBeforeReceiveMessageTicks = 0;
                public long TookToReceiveMessageTicks = 0;
                public long CpuUsageCycles = 0;
                public long TookToProcessMessageTicks = 0;
                public long TookToAnswerMessageTicks = 0;
                public long TookToAcknowledgeMessageTicks = 0;
                public long TookToCommitMessageProcessingTicks = 0;
                public long MessagesProcessed = 0;
            };
            private static void UpdateAverageStats(
                ProcessingStats average, ProcessingStats addition)
            {
                average.SleptBeforeReceiveMessageTicks = UpdateAverageValue(average.MessagesProcessed, average.SleptBeforeReceiveMessageTicks, addition.SleptBeforeReceiveMessageTicks);
                average.TookToReceiveMessageTicks = UpdateAverageValue(average.MessagesProcessed, average.TookToReceiveMessageTicks, addition.TookToReceiveMessageTicks);
                average.CpuUsageCycles = UpdateAverageValue(average.MessagesProcessed, average.CpuUsageCycles, addition.CpuUsageCycles);
                average.TookToProcessMessageTicks = UpdateAverageValue(average.MessagesProcessed, average.TookToProcessMessageTicks, addition.TookToProcessMessageTicks);
                average.TookToAnswerMessageTicks = UpdateAverageValue(average.MessagesProcessed, average.TookToAnswerMessageTicks, addition.TookToAnswerMessageTicks);
                average.TookToAcknowledgeMessageTicks = UpdateAverageValue(average.MessagesProcessed, average.TookToAcknowledgeMessageTicks, addition.TookToAcknowledgeMessageTicks);
                average.TookToCommitMessageProcessingTicks = UpdateAverageValue(average.MessagesProcessed, average.TookToCommitMessageProcessingTicks, addition.TookToCommitMessageProcessingTicks);
                average.MessagesProcessed++;
            }

            private static long UpdateAverageValue(long items, long averageValue, long addValue)
            {
                return ((averageValue * items) + addValue) / (items + 1);
            }

            private void PrintMessage(string message)
            {
                const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.ffffffzzz";
                var timestamp = DateTimeOffset.Now;
                Console.WriteLine($"{timestamp.ToString(TimestampFormat)} : {this.TaskId} : {message}");
            }

            public void PrintStats()
            {
                var stats = this.AverageStats;
                PrintMessage($"Stats: messagesProcessed={stats.MessagesProcessed}, sleptBeforeReceiveMessageTicks={stats.SleptBeforeReceiveMessageTicks},tookToReceiveMessageTicks={stats.TookToReceiveMessageTicks},tookToProcessMessageTicks={stats.TookToProcessMessageTicks},tookToAnswerMessageTicks={stats.TookToAnswerMessageTicks},tookToAcknowledgeMessageTicks={stats.TookToAcknowledgeMessageTicks},tookToCommitMessageProcessingTicks={stats.TookToCommitMessageProcessingTicks}");
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
