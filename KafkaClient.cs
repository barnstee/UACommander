﻿
namespace Opc.Ua.Cloud.Commander
{
    using Confluent.Kafka;
    using Newtonsoft.Json;
    using Org.BouncyCastle.Asn1.Ocsp;
    using Serilog;
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    public class KafkaClient
    {
        private ApplicationConfiguration _appConfig = null;

        private IProducer<Null, string> _producer = null;
        private IConsumer<Ignore, byte[]> _consumer = null;

        public KafkaClient(ApplicationConfiguration appConfig)
        {
            _appConfig = appConfig;
        }

        public void Connect()
        {
            try
            {
                // create Kafka client
                var config = new ProducerConfig {
                    BootstrapServers = Environment.GetEnvironmentVariable("BROKERNAME") + ":9093",
                    MessageTimeoutMs = 10000,
                    SecurityProtocol = SecurityProtocol.SaslSsl,
                    SaslMechanism = SaslMechanism.Plain,
                    SaslUsername = Environment.GetEnvironmentVariable("USERNAME"),
                    SaslPassword = Environment.GetEnvironmentVariable("PASSWORD")
                };

                _producer = new ProducerBuilder<Null, string>(config).Build();

                var conf = new ConsumerConfig
                {
                    GroupId = Environment.GetEnvironmentVariable("CLIENTNAME"),
                    BootstrapServers = Environment.GetEnvironmentVariable("BROKERNAME") + ":9093",
                    AutoOffsetReset = AutoOffsetReset.Earliest,
                    SecurityProtocol= SecurityProtocol.SaslSsl,
                    SaslMechanism = SaslMechanism.Plain,
                    SaslUsername = Environment.GetEnvironmentVariable("USERNAME"),
                    SaslPassword= Environment.GetEnvironmentVariable("PASSWORD")
                };

                _consumer = new ConsumerBuilder<Ignore, byte[]>(conf).Build();

                _consumer.Subscribe(new List<string>() {
                    Environment.GetEnvironmentVariable("CLIENTNAME") + ".command",
                    Environment.GetEnvironmentVariable("CLIENTNAME") + ".read",
                    Environment.GetEnvironmentVariable("CLIENTNAME") + ".write"
                });

                _ = Task.Run(() => HandleCommand());

                Log.Logger.Information("Connected to Kafka broker.");

            }
            catch (Exception ex)
            {
                Log.Logger.Error("Failed to connect to Kafka broker: " + ex.Message);
            }
        }

        public void Publish(string payload)
        {
            Message<Null, string> message = new()
            {
                Headers = new Headers() { { "Content-Type", Encoding.UTF8.GetBytes("application/json") } },
                Value = payload
            };

            _producer.ProduceAsync(Environment.GetEnvironmentVariable("CLIENTNAME") + ".response", message).GetAwaiter().GetResult();
        }

        // handles all incoming commands form the cloud
        private void HandleCommand()
        {
            while (true)
            {
                ResponseModel response = new()
                {
                    TimeStamp = DateTime.UtcNow,
                };

                try
                {
                    ConsumeResult<Ignore, byte[]> result = _consumer.Consume();

                    string requestPayload = Encoding.UTF8.GetString(result.Message.Value);
                    Log.Logger.Information($"Received method call with topic: {result.Topic} and payload: {requestPayload}");

                    // parse the message
                    RequestModel request = JsonConvert.DeserializeObject<RequestModel>(requestPayload);

                    // discard messages that are older than 15 seconds
                    if (request.TimeStamp < DateTime.UtcNow.AddSeconds(-15))
                    {
                        Log.Logger.Information($"Discarding old message with timestamp {request.TimeStamp}");
                        continue;
                    }

                    response.CorrelationId = request.CorrelationId;

                    // route this to the right handler
                    if (result.Topic.EndsWith(".command"))
                    {
                        new UAClient().ExecuteUACommand(_appConfig, requestPayload);
                        response.Success = true;
                    }
                    else if (result.Topic.EndsWith(".read"))
                    {
                        response.Status = new UAClient().ReadUAVariable(_appConfig, requestPayload);
                        response.Success = true;
                    }
                    else if (result.Topic.EndsWith(".write"))
                    {
                        new UAClient().WriteUAVariable(_appConfig, requestPayload);
                        response.Success = true;
                    }
                    else
                    {
                        Log.Logger.Error("Unknown command received: " + result.Topic);
                        response.Status = "Unkown command " + result.Topic;
                        response.Success = false;
                    }

                    // send reponse to Kafka broker
                    Publish(JsonConvert.SerializeObject(response));
                }
                catch (Exception ex)
                {
                    Log.Logger.Error(ex, "HandleMessageAsync");
                    response.Status = ex.Message;
                    response.Success = false;

                    // send error to Kafka broker
                    Publish(JsonConvert.SerializeObject(response));
                }
            }
        }
    }
}