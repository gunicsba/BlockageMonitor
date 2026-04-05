using System;

namespace BlockageMonitor
{
    public class PGN32300
    {
        // ISOBUS Seed Monitoring Data from Task Controller
        // PGN 32301 (0x7E2D in little endian: 0x2D, 0x7E)
        //0 HeaderLo                0x2D, 45
        //1 HeaderHi                0x7E, 126
        //2 Row ID                  0-255 (row number)
        //3 Population Rate Lo      DDI 12 - Actual Count Per Area Application Rate (low byte)
        //4 Population Rate Hi      DDI 12 - (high byte)
        //5 Skip Percentage         DDI 417 - Actual Seed Skip Percentage (0-100)
        //6 Multiple Percentage     DDI 419 - Actual Seed Multiple Percentage (0-100)
        //7 CRC

        private const byte cByteCount = 8;
        private const byte HeaderHi = 0x7E;
        private const byte HeaderLo = 0x2D;  // 0x7E2D = 32301
        private frmStart mf;
        private DateTime lastPlanterDataTime = DateTime.MinValue;
        private const int AUTO_SWITCH_DELAY_MS = 3000; // 3 seconds of consistent data before auto-switch

        public PGN32300(frmStart CF)
        {
            mf = CF;
        }

        public bool ParseByteData(byte[] data)
        {
            bool Result = false;

            if (data.Length >= cByteCount && data[1] == HeaderHi && data[0] == HeaderLo)
            {
                byte rowID = data[2];
                if (rowID < mf.SeedRows.Items.Count)
                {
                    // Parse DDI 12 - Population Rate (16-bit value)
                    ushort populationRaw = (ushort)(data[3] | (data[4] << 8));
                    double populationRate = populationRaw;

                    // Parse DDI 417 - Skip Percentage (0-100)
                    double skipPercentage = data[5];

                    // Parse DDI 419 - Multiple Percentage (0-100)
                    double multiplePercentage = data[6];

                    // Update the seed row data
                    mf.SeedRows.Items[rowID].PopulationRate = populationRate;
                    mf.SeedRows.Items[rowID].SkipPercentage = skipPercentage;
                    mf.SeedRows.Items[rowID].MultiplePercentage = multiplePercentage;
                    mf.SeedRows.Items[rowID].ReceiveTime = DateTime.Now;

                    // Auto-detect planter mode based on valid skip/multiple data
                    DetectPlanterMode(skipPercentage, multiplePercentage);

                    Result = true;
                }
            }

            return Result;
        }

        private void DetectPlanterMode(double skipPercentage, double multiplePercentage)
        {
            // Only auto-detect if enabled
            if (!mf.AutoDetectMode)
            {
                return;
            }

            // If we receive valid skip/multiple percentages (> 0 or < 100), this is likely a planter
            bool hasPlanterData = (skipPercentage > 0 && skipPercentage <= 100) ||
                                  (multiplePercentage > 0 && multiplePercentage <= 100);

            if (hasPlanterData)
            {
                if (!mf.IsPlanterMode)
                {
                    // Check if we've been receiving planter data consistently
                    if (lastPlanterDataTime == DateTime.MinValue)
                    {
                        // First planter data received
                        lastPlanterDataTime = DateTime.Now;
                    }
                    else if ((DateTime.Now - lastPlanterDataTime).TotalMilliseconds >= AUTO_SWITCH_DELAY_MS)
                    {
                        // Consistent planter data for 3 seconds, auto-switch to planter mode
                        mf.Invoke(new Action(() => {
                            mf.IsPlanterMode = true;
                        }));
                    }
                }
            }
            else
            {
                // No planter data detected, reset the timer
                lastPlanterDataTime = DateTime.MinValue;
            }
        }
    }
}
