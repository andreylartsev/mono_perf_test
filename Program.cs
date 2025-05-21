using Apache.NMS;
using Apache.NMS.Util;
using Mono.Unix;
using Mono.Unix.Native;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace ConsoleApp1
{
    [Cli.Doc(@"[[program]] recived message from source queue, doing some CPU-intensive usage operations replays with some outgoing messages to destination queue")]
    [Cli.GenerateSample]
    internal class Program
    {
        [Cli.Named]
        public bool PrintArgs = false;

        [Cli.Named]
        public bool UseListener = false;

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

        [Cli.Named]
        public Signum [] HandleSignals = { Signum.SIGUSR1, Signum.SIGUSR2 };

        public static void Main(string[] args)
        {
            Program program = new Program();
            try
            {
                Cli.ParseCommandLine(args, program);
                if (program.PrintArgs)
                    Cli.PrintArgs(program);
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
            catch (Cli.ArgumentParseException e)
            {
                Console.WriteLine(e.Message);
                Cli.PrintCommandLine(args);
                Cli.PrintUsage(program);
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

            if (IsUnixPlatform)
            {
                var signalHanlingTask = new SignalHandlingTask();
                signalHanlingTask.SignalsToWait = this.HandleSignals.ToArray();
                signalHanlingTask.CancellationTokenSource = cancellationTokenSource;
                allWaitHandlesList.Add(signalHanlingTask.EndEvent);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    signalHanlingTask.Exec();
                });
            }

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

        private static bool IsUnixPlatform => Environment.OSVersion.Platform == PlatformID.Unix;

        internal class SignalHandlingTask
        {
            public Signum[] SignalsToWait;
            public CancellationTokenSource CancellationTokenSource;
            public ManualResetEvent EndEvent = new ManualResetEvent(false);

            public SignalHandlingTask() { }

            public void Exec()
            {
                const Signum GracefullShutdownSignal = Signum.SIGTERM;
                try
                {
                    if (this.SignalsToWait == null)
                        throw new InvalidOperationException("SignalsToWait must not be null");


                    Console.WriteLine($"Started signal handling task:");
                    foreach (var signalNum in this.SignalsToWait)
                    {
                        Console.WriteLine($"Handle signal = {signalNum}");
                    }
                    var gracefullShutdownSignalHandler = new UnixSignal(GracefullShutdownSignal);
                    var signalList = new List<UnixSignal>();
                    signalList.Add(gracefullShutdownSignalHandler);
                    foreach (var sigNum in this.SignalsToWait)
                    {
                        try
                        {
                            var sigHandler = new UnixSignal(sigNum);
                            signalList.Add(sigHandler);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error during creating unix signal handling: sigNum={sigNum}, execption={ex.ToString()}");
                        }
                    }

                    var cancellationToken = this.CancellationTokenSource.Token;
                    var signalArray = signalList.ToArray();
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        const int WaitTimeout = 1000;
                        int timeTaken = UnixSignal.WaitAny(signalArray, WaitTimeout);
                        if (timeTaken < WaitTimeout)
                        {
                            foreach (var signal in signalArray)
                            {
                                if (signal.IsSet)
                                {
                                    if (signal.Signum == GracefullShutdownSignal)
                                    {
                                        Console.WriteLine($"Got gracefull shutdown signal #{signal.Signum}. cancellation");
                                        this.CancellationTokenSource.Cancel();
                                        signal.Reset();
                                    }
                                    else
                                    {
                                        Console.WriteLine($"Got signal #{signal.Signum}. ignoring");
                                        signal.Reset();
                                    }
                                }
                            }
                        }
                    }

                    Console.WriteLine($"Finished signal handling task");
                }
                finally
                {
                    EndEvent.Set();
                }
            }
        }



        private MessageReceiveTask MakeMessageReceiveTask(int taskId, CancellationToken cancellationToken)
        {
            MessageReceiveTask t = new MessageReceiveTask();
            t.TaskId = taskId;
            t.UseListener = this.UseListener;
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
            public bool UseListener = false;
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

            private IMessageProducer Producer = null;
            private ISession Session = null;
            private Random Random = new Random();

            public void Exec()
            {
                try
                {
                    try
                    {
                        if (this.RandomFactorForConnectionStartInMsecs > 0)
                        {
                            var waitFor = this.Random.Next(RandomFactorForConnectionStartInMsecs);
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

                            this.Session = connection.CreateSession(this.SessionAcknowledgmentMode);
                            try
                            {
                                if (this.Verbose)
                                    PrintMessage($"Created session in acknowledgment mode: {this.SessionAcknowledgmentMode}!");

                                if (this.Verbose)
                                    PrintMessage($"Using source: {this.Source}");

                                IDestination source = SessionUtil.GetDestination(this.Session, this.Source);
                                using (var consumer = this.Session.CreateConsumer(source))
                                {
                                    if (this.Verbose)
                                        PrintMessage($"And destination: {this.Destination}");
                                    IDestination destination = SessionUtil.GetDestination(this.Session, this.Destination);

                                    this.Producer = this.Session.CreateProducer(destination);
                                    try
                                    {

                                        this.Producer.DeliveryMode = this.MessageDeliveryMode;
                                        this.Producer.RequestTimeout = TimeSpan.FromSeconds(SendRequestTimeoutInSec);

                                        if (this.UseListener)
                                        {
                                            var messageListenerDelegate = new MessageListener(OnMessageReceive);
                                            consumer.Listener += messageListenerDelegate;
                                            WaitCancellationTookenLoop();
                                            consumer.Listener -= messageListenerDelegate;
                                        }
                                        else
                                        {
                                            MessageReceiveReplayLoop(consumer);
                                        }
                                    }
                                    finally
                                    {
                                        this.Producer?.Dispose();
                                        this.Producer = null;
                                        if (this.Verbose)
                                            PrintMessage("Producer closed!");
                                    }
                                }
                                if (this.Verbose)
                                    PrintMessage("Consumer closed!");
                            }
                            finally
                            {
                                this.Session?.Dispose();
                                this.Session = null;
                                if (this.Verbose)
                                    PrintMessage("Session closed!");
                            }
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

            private void WaitCancellationTookenLoop()
            {
                PrintMessage("Waiting cancellation token started!");
                while (true)
                {
                    var result = WaitHandle.WaitAny(new WaitHandle[] { this.CancellationToken.WaitHandle }, TimeSpan.FromSeconds(10));

                    if (this.CancellationToken.IsCancellationRequested)
                        break;

                    PrintMessage($"Not cancelled yet! Messages processed for a while {this.AverageStats.MessagesProcessed}");
                }
                PrintMessage("Waiting cancellation token finished!");
            }
            private void OnMessageReceive(IMessage incomingMessage)
            {
                if (this.CancellationToken.IsCancellationRequested)
                {
                    if (this.Verbose)
                        PrintMessage("Cancellation has is requested. Rolling back transaction");
                    this.Session?.Rollback();
                    return;
                }

                var stats = new ProcessingStats();

                stats.SleptBeforeReceiveMessageTicks = 0;
                stats.TookToReceiveMessageTicks = 0;

                var stopWatch = Stopwatch.StartNew();

                if (incomingMessage != null)
                {
                    ProcessMessage(this.Random, stats, this.Session, this.Producer, incomingMessage);
                    UpdateAverageStats(this.AverageStats, stats);
                }
                else
                {
                    if (this.Verbose)
                        PrintMessage("Got null message");
                }
            }

            private void MessageReceiveReplayLoop(IMessageConsumer consumer)
            {
                PrintMessage("Message receive loop has started!");
                while (!this.CancellationToken.IsCancellationRequested)
                {
                    var stats = new ProcessingStats();
                    var stopWatch = Stopwatch.StartNew();

                    if (this.RandomFactorForMessageReceiveInMsecs > 0)
                    {
                        var waitFor = this.Random.Next(this.RandomFactorForMessageReceiveInMsecs);
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
                        ProcessMessage(this.Random, stats, this.Session, this.Producer, incomingMessage);
                        UpdateAverageStats(this.AverageStats, stats);
                    }
                    else
                    {
                        if (this.Verbose)
                            PrintMessage($"No new messages to answer. Processed messages so far {this.AverageStats.MessagesProcessed}");
                    }
                }
                PrintMessage("Message receive loop has finished!");
            }

            private void ProcessMessage(Random random, ProcessingStats stats, ISession session, IMessageProducer producer, IMessage incomingMessage)
            {
                var stopWatch = Stopwatch.StartNew();

                var incomingText = (incomingMessage is ITextMessage) ? (incomingMessage as ITextMessage).Text : "non-text messaage";
                if (this.PrintMessages)
                {
                    var nmsMessageId = incomingMessage.NMSMessageId;
                    PrintMessage($"<-- {incomingText}/{nmsMessageId}");
                }

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
