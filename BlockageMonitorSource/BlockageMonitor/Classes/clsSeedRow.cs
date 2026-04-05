using System;

namespace BlockageMonitor
{
    public class clsSeedRow
    {
        private bool cEdited;
        private bool cEnabled;
        private int cID;
        private bool cNotified;
        private byte cRate;
        private byte cRateAve;
        private DateTime cReceiveTime;
        private frmStart mf;
        private string Name;

        // ISOBUS DDI values for seed monitoring
        private double cPopulationRate;      // DDI 12 - Actual Count Per Area Application Rate
        private double cSkipPercentage;      // DDI 417 - Actual Seed Skip Percentage
        private double cMultiplePercentage;  // DDI 419 - Actual Seed Multiple Percentage

        public clsSeedRow(frmStart CF, int ID)
        {
            mf = CF;
            cID = ID;
            Name = "SeedRow" + ID.ToString();
            cEnabled = true;
            cPopulationRate = 0;
            cSkipPercentage = 0;
            cMultiplePercentage = 0;
        }

        public bool Enabled
        {
            get { return cEnabled; }
            set
            {
                if (cEnabled != value) cEdited = true;
                cEnabled = value;
            }
        }
        public int ModuleID
        {
            get
            {
                int Result = 0;
                foreach (clsModule Md in mf.BlockageModules.Items)
                {
                    if ((ID+1) >= Md.StartRow && (ID+1) <= Md.EndRow)
                    {
                        Result = Md.ID;
                        break;
                    }
                }
                return Result;
            }
        }

        public int ID
        { get { return cID; } }

        public bool Notified
        { get { return cNotified; } set { cNotified = value; } }

        public byte Rate
        {
            get { return cRate; }
            set
            {
                cRate = value;
                if (value > 0)
                {
                    cRateAve = (byte)(cRateAve * 0.8 + value * 0.2);
                }
                else
                {
                    cRateAve = 0;
                }
            }
        }

        public byte RateAverage
        { get {  return cRateAve; } }

        public DateTime ReceiveTime
        { get { return cReceiveTime; } set { cReceiveTime = value; } }

        public bool Blocked()
        {
            bool Result = false;
            double Sec = (DateTime.Now - ReceiveTime).TotalSeconds;
            Result = (Sec > mf.BlockSeconds);
            if (!Result) Notified = false;  // reset notifed
            return Result;
        }

        public void Load()
        {
            if (bool.TryParse(mf.Tls.LoadProperty(Name + "_Enabled"), out bool en)) cEnabled = en;
        }

        public void Save()
        {
            if (cEdited)
            {
                mf.Tls.SaveProperty(Name + "_Enabled", cEnabled.ToString());
                cEdited = false;
            }
        }

        // ISOBUS DDI property accessors
        public double PopulationRate
        {
            get { return cPopulationRate; }
            set { cPopulationRate = value; }
        }

        public double SkipPercentage
        {
            get { return cSkipPercentage; }
            set { cSkipPercentage = value; }
        }

        public double MultiplePercentage
        {
            get { return cMultiplePercentage; }
            set { cMultiplePercentage = value; }
        }

        // Calculated values for stacked chart display
        public double NormalPopulation
        {
            get
            {
                // Normal = Population * (1 - Skip% - Multiple%)
                double normalFactor = Math.Max(0, 1.0 - (cSkipPercentage / 100.0) - (cMultiplePercentage / 100.0));
                return cPopulationRate * normalFactor;
            }
        }

        public double SkippedPopulation
        {
            get
            {
                // Skipped portion = Population * Skip%
                return cPopulationRate * (cSkipPercentage / 100.0);
            }
        }

        public double MultiplePopulation
        {
            get
            {
                // Multiple portion = Population * Multiple%
                return cPopulationRate * (cMultiplePercentage / 100.0);
            }
        }
    }
}