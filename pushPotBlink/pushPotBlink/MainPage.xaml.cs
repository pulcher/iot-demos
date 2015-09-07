using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.Gpio;
using Windows.Devices.Spi;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace pushPotBlink
{
    public sealed partial class MainPage : Page
    {
        private const int LED_BUTTON_PIN = 13;
        private const int BUTTON_PIN = 6;
        private const int LED_BLINK_PIN = 5;
        private const byte MCP3002_CONFIG = 0x68; /* 01101000 channel configuration data for the MCP3002 */
        private GpioPin ledButtonPin;
        private GpioPin buttonPin;
        private GpioPin ledBlinkPin;
        private GpioPinValue ledButtonPinValue = GpioPinValue.High;
        private const string SPI_CONTROLLER_NAME = "SPI0";  /* Friendly name for Raspberry Pi 2 SPI controller          */
        private const Int32 SPI_CHIP_SELECT_LINE = 0;       /* Line 0 maps to physical pin number 24 on the Rpi2        */
        private SpiDevice SpiADC;
        private DispatcherTimer timer;
        private Timer periodicTimer;
        private int adcValue;
        private SolidColorBrush redBrush = new SolidColorBrush(Windows.UI.Colors.Red);
        private SolidColorBrush greenBrush = new SolidColorBrush(Windows.UI.Colors.Green);
        private SolidColorBrush grayBrush = new SolidColorBrush(Windows.UI.Colors.LightGray);

        public MainPage()
        {
            this.InitializeComponent();

            InitAll();
        }

        private void BlinkTimer_Tick(object sender, object e)
        {
            FlipLed(ledBlinkPin);
            var task = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                var test = LedBlink.Fill;

                LedBlink.Fill = (ledBlinkPin.Read() == GpioPinValue.Low) ?
                        greenBrush : grayBrush;
            });

            timer.Interval = new TimeSpan(0, 0, 0, 0, adcValue);
        }

        private void FlipLed(GpioPin led)
        {
            if (adcValue == 0)
            {
                turnOnLed(led);
                return;
            }
            if (led.Read() == GpioPinValue.High)
                turnOnLed(led);
            else
                turnOffLed(led);
        }

        private void turnOnLed(GpioPin led)
        {
            led.Write(GpioPinValue.Low);
        }

        private void turnOffLed(GpioPin led)
        {
            led.Write(GpioPinValue.High);
        }


        private async Task InitAll()
        {
            try
            {
                InitGPIO();         /* Initialize GPIO to toggle the LED                          */
                await InitSPI();    /* Initialize the SPI bus for communicating with the ADC      */

            }
            catch (Exception ex)
            {
                GpioStatus.Text = ex.Message;
                return;
            }

            /* Now that everything is initialized, create a timer so we read data every 100mS */
            periodicTimer = new Timer(this.PotTimer_Tick, null, 0, 100);

            timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(adcValue);
            timer.Tick += BlinkTimer_Tick;

            if (buttonPin != null)
                timer.Start();

            GpioStatus.Text += " Blink running";
        }

        private void InitGPIO()
        {
            GpioController gpio;
            try
            {
                gpio = GpioController.GetDefault();
            }
            catch (Exception)
            {
                GpioStatus.Text = "There is no GPIO controller on this device.";
                return;
            }

            // setup the button and it's pins
            buttonPin = gpio.OpenPin(BUTTON_PIN);
            ledButtonPin = gpio.OpenPin(LED_BUTTON_PIN);

            ledButtonPin.Write(GpioPinValue.High);
            ledButtonPin.SetDriveMode(GpioPinDriveMode.Output);

            if (buttonPin.IsDriveModeSupported(GpioPinDriveMode.InputPullUp))
                buttonPin.SetDriveMode(GpioPinDriveMode.InputPullUp);
            else
                buttonPin.SetDriveMode(GpioPinDriveMode.Input);

            buttonPin.DebounceTimeout = TimeSpan.FromMilliseconds(50);
            buttonPin.ValueChanged += buttonPin_ValueChanged;

            // setup blinking led
            ledBlinkPin = gpio.OpenPin(LED_BLINK_PIN);

            ledBlinkPin.Write(GpioPinValue.High);
            ledBlinkPin.SetDriveMode(GpioPinDriveMode.Output);

            ButtonStatus.Text = "Released";

            GpioStatus.Text = "GPIO pins initialized correctly.";
        }

        private async Task InitSPI()
        {
            try
            {
                var settings = new SpiConnectionSettings(SPI_CHIP_SELECT_LINE);
                settings.ClockFrequency = 500000;   /* 0.5MHz clock rate                                        */
                settings.Mode = SpiMode.Mode0;      /* The ADC expects idle-low clock polarity so we use Mode0  */

                string spiAqs = SpiDevice.GetDeviceSelector(SPI_CONTROLLER_NAME);
                var deviceInfo = await DeviceInformation.FindAllAsync(spiAqs);
                SpiADC = await SpiDevice.FromIdAsync(deviceInfo[0].Id, settings);
            }

            catch (Exception ex)
            {
                throw new Exception("SPI Initialization Failed", ex);
            }
        }

        private void buttonPin_ValueChanged(GpioPin sender, GpioPinValueChangedEventArgs e)
        {
            // toggle the state of the LED every time the button is pressed
            if (e.Edge == GpioPinEdge.FallingEdge)
            {
                ledButtonPinValue = (ledButtonPinValue == GpioPinValue.Low) ?
                    GpioPinValue.High : GpioPinValue.Low;
                ledButtonPin.Write(ledButtonPinValue);
            }

            // need to invoke UI updates on the UI thread because this event
            // handler gets invoked on a separate thread.
            var task = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (e.Edge == GpioPinEdge.FallingEdge)
                {
                    LedButton.Fill = (ledButtonPinValue == GpioPinValue.Low) ?
                        redBrush : grayBrush;
                    ButtonStatus.Text = "Pressed";
                }
                else
                {
                    ButtonStatus.Text = "Released";
                }
            });
        }

        private void PotTimer_Tick(object state)
        {
            ReadADC();         
        }

        public void ReadADC()
        {
            byte[] readBuffer = new byte[3]; /* Buffer to hold read data*/
            byte[] writeBuffer = new byte[3] { 0x00, 0x00, 0x00 };

            writeBuffer[0] = MCP3002_CONFIG;

            SpiADC.TransferFullDuplex(writeBuffer, readBuffer); /* Read data from the ADC                           */
            adcValue = convertToInt(readBuffer);                /* Convert the returned bytes into an integer value */

            /* UI updates must be invoked on the UI thread */
            var task = this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                PotSetting.Text = adcValue.ToString();         /* Display the value on screen                      */
            });
        }

        public int convertToInt(byte[] data)
        {
            int result = 0;
            result = data[0] & 0x03;
            result <<= 8;
            result += data[1];
            return result;
        }
    }
}
