using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;

namespace BlockageSimulator
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SimulatorForm());
        }
    }

    public class SimulatorForm : Form
    {
        private Button btnSeederMode;
        private Button btnPlanterMode;
        private Button btnStop;
        private Label lblStatus;
        private ComboBox cmbNetworkInterface;
        private Label lblNetwork;
        private System.Windows.Forms.Timer sendTimer;
        private UdpClient udpClient;
        private IPEndPoint targetEndpoint;
        private IPAddress broadcastAddress;
        private bool isRunning = false;
        private int simulationMode = 0; // 0 = None, 1 = Seeder, 2 = Planter
        private int rowCount = 16;
        private Random random = new Random();

        public SimulatorForm()
        {
            Text = "BlockageMonitor Simulator";
            Size = new System.Drawing.Size(450, 350);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;

            // Network Interface Picker
            lblNetwork = new Label
            {
                Text = "Network Interface:",
                Location = new System.Drawing.Point(20, 15),
                Size = new System.Drawing.Size(130, 20)
            };

            cmbNetworkInterface = new ComboBox
            {
                Location = new System.Drawing.Point(150, 12),
                Size = new System.Drawing.Size(260, 24),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            cmbNetworkInterface.SelectedIndexChanged += CmbNetworkInterface_SelectedIndexChanged;

            btnSeederMode = new Button
            {
                Text = "Start Seeder Mode",
                Location = new System.Drawing.Point(20, 50),
                Size = new System.Drawing.Size(180, 40)
            };
            btnSeederMode.Click += BtnSeederMode_Click;

            btnPlanterMode = new Button
            {
                Text = "Start Planter Mode",
                Location = new System.Drawing.Point(230, 50),
                Size = new System.Drawing.Size(180, 40)
            };
            btnPlanterMode.Click += BtnPlanterMode_Click;

            btnStop = new Button
            {
                Text = "Stop",
                Location = new System.Drawing.Point(20, 100),
                Size = new System.Drawing.Size(390, 40),
                Enabled = false
            };
            btnStop.Click += BtnStop_Click;

            lblStatus = new Label
            {
                Text = "Status: Stopped\nTarget: 127.0.0.1",
                Location = new System.Drawing.Point(20, 160),
                Size = new System.Drawing.Size(390, 80),
                BorderStyle = BorderStyle.FixedSingle
            };

            Controls.Add(lblNetwork);
            Controls.Add(cmbNetworkInterface);
            Controls.Add(btnSeederMode);
            Controls.Add(btnPlanterMode);
            Controls.Add(btnStop);
            Controls.Add(lblStatus);

            sendTimer = new System.Windows.Forms.Timer();
            sendTimer.Interval = 500; // Send every 500ms
            sendTimer.Tick += SendTimer_Tick;

            udpClient = new UdpClient();
            targetEndpoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 25600); // BlockageMonitor receive port
            broadcastAddress = IPAddress.Parse("127.0.0.1");

            // Populate network interfaces after all controls are created
            PopulateNetworkInterfaces();
        }

        private void PopulateNetworkInterfaces()
        {
            cmbNetworkInterface.Items.Clear();
            cmbNetworkInterface.Items.Add(new NetworkInterfaceItem
            {
                Name = "Loopback (127.0.0.1)",
                Broadcast = IPAddress.Parse("127.0.0.1"),
                Address = IPAddress.Parse("127.0.0.1")
            });

            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus == OperationalStatus.Up &&
                        (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                         ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211))
                    {
                        foreach (UnicastIPAddressInformation ip in ni.GetIPProperties().UnicastAddresses)
                        {
                            if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                            {
                                // Calculate broadcast address
                                byte[] ipBytes = ip.Address.GetAddressBytes();
                                byte[] maskBytes = ip.IPv4Mask.GetAddressBytes();
                                byte[] broadcastBytes = new byte[4];
                                for (int i = 0; i < 4; i++)
                                {
                                    broadcastBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
                                }
                                IPAddress broadcast = new IPAddress(broadcastBytes);

                                cmbNetworkInterface.Items.Add(new NetworkInterfaceItem
                                {
                                    Name = $"{ni.Name} ({ip.Address})",
                                    Broadcast = broadcast,
                                    Address = ip.Address
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error enumerating network interfaces: {ex.Message}", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            if (cmbNetworkInterface.Items.Count > 0)
                cmbNetworkInterface.SelectedIndex = 0;
        }

        private void CmbNetworkInterface_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (cmbNetworkInterface.SelectedItem is NetworkInterfaceItem item)
            {
                broadcastAddress = item.Broadcast;
                UpdateStatus();
            }
        }

        private void UpdateStatus()
        {
            string modeText = simulationMode == 0 ? "Stopped" :
                             simulationMode == 1 ? "Seeder Mode (PGN 32100)" : "Planter Mode (PGN 32301)";
            lblStatus.Text = $"Status: {modeText}\nTarget: {broadcastAddress}\nPort: 25600 (Seeder) / 25800 (Planter)\nPGN: 32100 (Seeder) / 32301 (Planter)";
        }

        private void BtnSeederMode_Click(object sender, EventArgs e)
        {
            simulationMode = 1;
            isRunning = true;
            btnSeederMode.Enabled = false;
            btnPlanterMode.Enabled = false;
            cmbNetworkInterface.Enabled = false;
            btnStop.Enabled = true;
            UpdateStatus();
            sendTimer.Start();
        }

        private void BtnPlanterMode_Click(object sender, EventArgs e)
        {
            simulationMode = 2;
            isRunning = true;
            btnSeederMode.Enabled = false;
            btnPlanterMode.Enabled = false;
            cmbNetworkInterface.Enabled = false;
            btnStop.Enabled = true;
            UpdateStatus();
            sendTimer.Start();
        }

        private void BtnStop_Click(object sender, EventArgs e)
        {
            isRunning = false;
            simulationMode = 0;
            sendTimer.Stop();
            btnSeederMode.Enabled = true;
            btnPlanterMode.Enabled = true;
            cmbNetworkInterface.Enabled = true;
            btnStop.Enabled = false;
            UpdateStatus();
        }

        private void SendTimer_Tick(object sender, EventArgs e)
        {
            if (!isRunning) return;

            for (int row = 0; row < rowCount; row++)
            {
                if (simulationMode == 1)
                {
                    SendSeederData(row);
                }
                else if (simulationMode == 2)
                {
                    SendPlanterData(row);
                }
                Thread.Sleep(10); // Small delay between rows
            }
        }

        private void SendSeederData(int row)
        {
            // PGN 32100 format:
            // 0: HeaderLo (0x64)
            // 1: HeaderHi (0x7D)
            // 2: RowID (0-15) in upper nibble + ModuleID (0-15) in lower nibble
            // 3: Rate (0-255)
            // 4: CRC (sum of bytes 0-3)

            byte[] data = new byte[5];
            data[0] = 0x64; // HeaderLo
            data[1] = 0x7D; // HeaderHi
            data[2] = (byte)((row << 4) | 0); // Row in upper nibble, Module 0 in lower nibble
            // Simulate varying rates - some rows with low rates (blockage)
            int baseRate = 80;
            if (row == 5 || row == 6) baseRate = 20; // Simulate blockage on rows 5-6
            if (row == 12) baseRate = 0; // Complete blockage on row 12
            data[3] = (byte)(baseRate + random.Next(-10, 10));
            // Calculate CRC as sum of bytes 0-3
            data[4] = (byte)(data[0] + data[1] + data[2] + data[3]);

            udpClient.Send(data, data.Length, new IPEndPoint(broadcastAddress, 25600));
        }

        private void SendPlanterData(int row)
        {
            // PGN 32301 format:
            // 0: HeaderLo (0x2D) - 0x7E2D = 32301
            // 1: HeaderHi (0x7E)
            // 2: Row ID (0-255)
            // 3-4: Population Rate (DDI 12, 16-bit)
            // 5: Skip Percentage (DDI 417, 0-100)
            // 6: Multiple Percentage (DDI 419, 0-100)
            // 7: CRC

            byte[] data = new byte[8];
            data[0] = 0x2D; // HeaderLo (45) - PGN 32301 = 0x7E2D
            data[1] = 0x7E; // HeaderHi (126)
            data[2] = (byte)row;

            // Population rate (seeds per area) - typical range 50-150
            ushort population = (ushort)(100 + random.Next(-20, 20));
            data[3] = (byte)(population & 0xFF);
            data[4] = (byte)((population >> 8) & 0xFF);

            // Skip and Multiple percentages - simulate some rows with issues
            byte skipPercent = 0;
            byte multiplePercent = 0;

            if (row == 3 || row == 4)
            {
                // Rows 3-4 have skip issues (missed seeds)
                skipPercent = (byte)(15 + random.Next(-5, 5));
            }
            else if (row == 8 || row == 9 || row == 10)
            {
                // Rows 8-10 have double seed issues
                multiplePercent = (byte)(20 + random.Next(-5, 5));
            }
            else if (row == 14)
            {
                // Row 14 has both issues
                skipPercent = 10;
                multiplePercent = 12;
            }

            data[5] = skipPercent;
            data[6] = multiplePercent;
            // Calculate CRC as sum of bytes 0-6
            byte crc = 0;
            for (int i = 0; i < 7; i++) crc += data[i];
            data[7] = crc;

            udpClient.Send(data, data.Length, new IPEndPoint(broadcastAddress, 25800)); // SeedMonitor port
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            isRunning = false;
            sendTimer?.Stop();
            udpClient?.Close();
            base.OnFormClosing(e);
        }
    }

    public class NetworkInterfaceItem
    {
        public string Name { get; set; }
        public IPAddress Broadcast { get; set; }
        public IPAddress Address { get; set; }

        public override string ToString()
        {
            return Name;
        }
    }
}
