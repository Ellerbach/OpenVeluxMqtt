using Iot.Device.Am2320;
using System.Device.I2c;
using nanoFramework.Hardware.Esp32;
using nanoFramework.M2Mqtt;
using nanoFramework.M2Mqtt.Messages;
using nanoFramework.Networking;
using System;
using System.Device.Gpio;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace OpenVeluxMqtt
{
    public class Program
    {
        // Wifi credentials
        private const string Ssid = "ssid";
        private const string Password = "password";
        private const string MqttServer = "192.168.1.2";

        // MQTT topics
        private const string TopicTemperature = "sensor/temperature";
        private const string TopicHumidity = "sensor/humidity";
        private const string TopicSubscribe = "velux/action";
        private const string VeluxState = "velux/state";
        private const string TopicVelux = "velux/percent";
        private const string MessageMqttOpen = "ON";
        private const string MessageMqttClosed = "OFF";
        private const string DeviceName = "Velux2";

        // AM2320 I2C Pin for temp and humidity
        private const int PinI2cData = 12;
        private const int PinI2cClock = 14;
        // The remote control button
        private const int ButtonUpLeft = 23;
        private const int ButtonUpMiddleUp = 22;
        private const int ButtonUpRight = 21;
        private const int ButtonUpMiddleDown = 19;
        private const int ButtonMiddleUp = 18;
        private const int ButtonMiddleMiddle = 5;
        private const int ButtonMiddleDown = 17;
        // Delay to wait for a click
        private const int ClickLength = 300;
        // Reset pin is actually not the button on the remote
        // It is aimed to reset the remote control and
        // arrive in a known state
        private const int ButtonReset = 16;
        private const int TimeToRebootRemoteSeconds = 12;

        private const int NumberOfWindows = 5;
        private const int AllWindows = 4;
        private const int TimeToProcessOperation = 28000;

        // To store the last operation
        private static bool[] LastAction = new bool[NumberOfWindows];        
        private static int currentWindowsNumber = 0;

        private static bool _isBuzy = false;
        private static GpioController _controller;
        private static Am2320 _am;
        private static MqttClient _mqtt;
        private static Timer _timer;

        public static void Main()
        {
            Debug.WriteLine("Hello from nanoFramework!");
            _controller = new GpioController();
            // Configure I2C
            Configuration.SetPinFunction(PinI2cData, DeviceFunction.I2C1_DATA);
            Configuration.SetPinFunction(PinI2cClock, DeviceFunction.I2C1_CLOCK);
            _am = new(new I2cDevice(new I2cConnectionSettings(1, Am2320.DefaultI2cAddress)));
            _controller.OpenPin(ButtonMiddleDown, PinMode.Output);
            _controller.Write(ButtonMiddleDown, PinValue.Low);
            _controller.OpenPin(ButtonUpLeft, PinMode.Output);
            _controller.Write(ButtonUpLeft, PinValue.Low);
            _controller.OpenPin(ButtonMiddleMiddle, PinMode.Output);
            _controller.Write(ButtonMiddleMiddle, PinValue.Low);
            _controller.OpenPin(ButtonMiddleUp, PinMode.Output);
            _controller.Write(ButtonMiddleUp, PinValue.Low);
            _controller.OpenPin(ButtonUpMiddleDown, PinMode.Output);
            _controller.Write(ButtonUpMiddleDown, PinValue.Low);
            _controller.OpenPin(ButtonUpMiddleUp, PinMode.Output);
            _controller.Write(ButtonUpMiddleUp, PinValue.Low);
            _controller.OpenPin(ButtonUpRight, PinMode.Output);
            _controller.Write(ButtonUpRight, PinValue.Low);

            // Reset the remote control
            _controller.OpenPin(ButtonReset, PinMode.Output);
            Reset();
            DateTime dtRebootTime = DateTime.UtcNow.AddSeconds(TimeToRebootRemoteSeconds);

            // Connect to wifi
            if (!ConnectToWifi())
            {
                Debug.WriteLine("Can't connect to wifi");
                SetupDeepSleepAndRetry();
            }

            // Make sure the remote had time to reboot
            while (dtRebootTime > DateTime.UtcNow)
            {
                Thread.Sleep(20);
            }

            // Connect to the MQTT Server
            _mqtt = new(MqttServer, 1883, false, null, null, MqttSslProtocols.None);
            TryReconnectMqtt();

            _mqtt.ConnectionClosed += MqttConnectionClosed;
            _mqtt.MqttMsgPublishReceived += MqttMsgPublishReceived;
            SubscribeTopics();

            // Close all windows
            _isBuzy = true;
            CloseWindow(AllWindows);

            // Setup the timer to publish the states every minute
            _timer = new(TimerCallBackPublish, null, 0, 60000);

            Thread.Sleep(Timeout.Infinite);
        }

        private static void MqttMsgPublishReceived(object sender, MqttMsgPublishEventArgs e)
        {
            string message = Encoding.UTF8.GetString(e.Message, 0, e.Message.Length);

            int number = e.Topic[TopicSubscribe.Length] - '0';
            if ((number < 0) || (number > NumberOfWindows))
            {
                Debug.WriteLine($"Windows number not correct: {number}");
                return;
            }

            Debug.WriteLine($"Message from {number}: {message}");
            if (message == MessageMqttOpen)
            {
                if (!LastAction[number])
                {
                    while (_isBuzy)
                    {
                        Thread.Sleep(1000);
                    }

                    _isBuzy = true;
                    OpenWindow(number);
                }
            }
            else
            {
                if (LastAction[number])
                {
                    while (_isBuzy)
                    {
                        Thread.Sleep(1000);
                    }

                    _isBuzy = true;
                    CloseWindow(number);
                }
            }
        }

        private static void SubscribeTopics()
        {
            _mqtt.Subscribe(new string[] {
                $"{TopicSubscribe}0",
                $"{TopicSubscribe}1",
                $"{TopicSubscribe}2",
                $"{TopicSubscribe}3",
                $"{TopicSubscribe}4",
            }, new MqttQoSLevel[] {
                MqttQoSLevel.AtMostOnce,
                MqttQoSLevel.AtMostOnce,
                MqttQoSLevel.AtMostOnce,
                MqttQoSLevel.AtMostOnce,
                MqttQoSLevel.AtMostOnce,
            });
        }

        private static void MqttConnectionClosed(object sender, EventArgs e)
        {
            TryReconnectMqtt();
        }

        private static void TimerCallBackPublish(object state)
        {
            if (!_mqtt.IsConnected)
            {
                TryReconnectMqtt();
            }

            var hum = _am.Humidity;
            var temp = _am.Temperature;
            if (_am.IsLastReadSuccessful)
            {
                Debug.WriteLine($"Temp: {temp.DegreesCelsius}, Hum: {hum.Percent}");
                _mqtt.Publish(TopicHumidity, Encoding.UTF8.GetBytes($"{hum.Percent}"));
                _mqtt.Publish(TopicTemperature, Encoding.UTF8.GetBytes($"{temp.DegreesCelsius}"));
            }
            else
            {
                Debug.WriteLine("Can't properly read the DHT22");
            }

            for (int i = 0; i < NumberOfWindows; i++)
            {
                PublishState(i, LastAction[i] ? 100 : 0);
            }
        }

        private static void TryReconnectMqtt()
        {
            // Try to reconnect right away
            MqttReasonCode ret;
            int retry = 0;
        Reconnect:
            ret = _mqtt.Connect(DeviceName);
            if (ret != MqttReasonCode.Success)
            {
                // Give it quite some retry
                if (retry++ > 10)
                {
                    SetupDeepSleepAndRetry();
                }

                // Wait 30 seconds
                Thread.Sleep(30000);
                goto Reconnect;
            }

            SubscribeTopics();
        }

        private static void SetupDeepSleepAndRetry()
        {
            // Sleep for 2 seconds and retry
            Sleep.EnableWakeupByTimer(TimeSpan.FromSeconds(2));
            Sleep.StartDeepSleep();
        }

        private static void Reset()
        {
            _controller.Write(ButtonReset, PinValue.Low);
            Thread.Sleep(500);
            _controller.Write(ButtonReset, PinValue.High);
        }

        private static void ButtonSelectWindow(bool down)
        {
            if (down)
            {
                _controller.Write(ButtonUpMiddleDown, PinValue.High);
                Thread.Sleep(ClickLength);
                _controller.Write(ButtonUpMiddleDown, PinValue.Low);
            }
            else
            {
                _controller.Write(ButtonUpMiddleUp, PinValue.High);
                Thread.Sleep(ClickLength);
                _controller.Write(ButtonUpMiddleUp, PinValue.Low);
            }
        }

        private static void SelectWindows(int number)
        {
            Debug.WriteLine($"Current window is: {currentWindowsNumber}");
            int toMove = number - currentWindowsNumber;
            bool direction = false;
            Debug.WriteLine($"To move: {toMove}  and direction: {direction}");

            if (toMove > 0)
            {
                direction = true;
            }
            else
            {
                toMove = -toMove;
            }

            for (int i = 0; i < toMove; i++)
            {
                ButtonSelectWindow(direction);
                Thread.Sleep(ClickLength * 2);
            }

            currentWindowsNumber = number;
        }

        private static void OpenWindow(int number)
        {
            Debug.WriteLine($"Opening velux {number}");
            LastAction[number] = true;
            if (number == AllWindows)
            {
                for (int i = 0; i < NumberOfWindows; i++)
                {
                    LastAction[i] = true;
                }
            }
            SelectWindows(number);
            _controller.Write(ButtonMiddleUp, PinValue.High);
            Thread.Sleep(ClickLength);
            _controller.Write(ButtonMiddleUp, PinValue.Low);
            PublishState(number, 100);
            Thread.Sleep(TimeToProcessOperation);
            _isBuzy = false;
        }

        private static void CloseWindow(int number)
        {
            Debug.WriteLine($"Closing velux {number}");
            LastAction[number] = false;
            if (number == AllWindows)
            {
                for (int i = 0; i < NumberOfWindows; i++)
                {
                    LastAction[i] = false;
                }
            }
            SelectWindows(number);
            _controller.Write(ButtonMiddleDown, PinValue.High);
            Thread.Sleep(ClickLength);
            _controller.Write(ButtonMiddleDown, PinValue.Low);
            PublishState(number, 0);
            Thread.Sleep(TimeToProcessOperation);
            _isBuzy = false;
        }

        private static void PublishState(int number, int state)
        {
            if ((_mqtt == null) || !_mqtt.IsConnected)
            {
                return;
            }

            string topic = $"{TopicVelux}{number}";
            _mqtt.Publish(topic, Encoding.UTF8.GetBytes($"{state}"));
            topic = $"{VeluxState}{number}";
            if (state > 10)
            {
                _mqtt.Publish(topic, Encoding.UTF8.GetBytes(MessageMqttOpen));
            }
            else
            {
                _mqtt.Publish(topic, Encoding.UTF8.GetBytes(MessageMqttClosed));
            }
        }

        private static bool ConnectToWifi()
        {
            Debug.WriteLine("Program Started, connecting to WiFi.");

            // As we are using TLS, we need a valid date & time
            // We will wait maximum 1 minute to get connected and have a valid date
            var success = WiFiNetworkHelper.ConnectDhcp(Ssid, Password, requiresDateTime : true, token: new CancellationTokenSource(60000).Token);
            if (!success)
            {
                if (WiFiNetworkHelper.HelperException != null)
                {
                    Debug.WriteLine($"{WiFiNetworkHelper.HelperException.Message}");
                }
            }

            Debug.WriteLine($"Date and time is now {DateTime.UtcNow}");
            return success;
        }
    }
}
