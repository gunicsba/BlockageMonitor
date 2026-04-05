# BlockageMonitor

A Windows Forms application for monitoring seed planting data received via UDP. Displays data from seeders, planters, or any implement that sends compatible PGN messages.

## Features

- **Dual Mode Support**: Toggle between Seeder mode (blockage monitoring) and Planter mode (population/skip/double monitoring)
- **Visual Average Line**: White line with black outline showing the average across all rows
- **Stacked Bar Chart** (Planter mode): Shows Normal (green), Multiple (orange), and Skipped/Missed (red) seed populations
- **Simple Bar Chart** (Seeder mode): Shows seed rate for each row
- **Auto-detection**: Automatically detects Planter mode based on incoming data format

## Supported PGNs

### PGN 32100 - Seeder Data (Blockage Monitoring)

Used for traditional blockage monitoring in air seeders and grain drills.

**PGN:** 32100 (0x7D64)

**Port:** 25600 (UDP)

**Data Format (5 bytes):**

| Byte | Description | Value |
|------|-------------|-------|
| 0 | HeaderLo | 0x64 (100) |
| 1 | HeaderHi | 0x7D (125) |
| 2 | RowID/ModuleID | Upper nibble: Row ID (0-15), Lower nibble: Module ID (0-15) |
| 3 | Rate | Seed rate (0-255) |
| 4 | CRC | Sum of bytes 0-3 |

**Example:**
```
0x64, 0x7D, 0x10, 0x50, 0xDB
```
- Row 1 (0x10 >> 4 = 1), Module 0 (0x10 & 0x0F = 0)
- Rate = 80 (0x50)
- CRC = 0x64 + 0x7D + 0x10 + 0x50 = 0xDB (219)

### PGN 32301 - Planter Data

Used for precision planting data showing population, skips, and multiples. Compatible with ISOBUS Task Controller data.

**PGN:** 32301 (0x7E2D)

**Port:** 25800 (UDP)

**Data Format (8 bytes):**

| Byte | Description | Value | ISOBUS DDI |
|------|-------------|-------|------------|
| 0 | HeaderLo | 0x2D (45) | - |
| 1 | HeaderHi | 0x7E (126) | - |
| 2 | Row ID | Row number (0-255) | - |
| 3 | Population Rate Lo | Low byte (seeds per area) | [DDI 12](https://www.isobus.net/isobus/dal/DDI/12) |
| 4 | Population Rate Hi | High byte | [DDI 12](https://www.isobus.net/isobus/dal/DDI/12) |
| 5 | Skip Percentage | Percentage of skipped seeds (0-100) | [DDI 417](https://www.isobus.net/isobus/dal/DDI/417) |
| 6 | Multiple Percentage | Percentage of multiple seeds (0-100) | [DDI 419](https://www.isobus.net/isobus/dal/DDI/419) |
| 7 | CRC | Sum of bytes 0-6 | - |

**ISOBUS DDIs:**
- **[DDI 12](https://www.isobus.net/isobus/dal/DDI/12)** (0x000C): Actual Count Per Area Application Rate
- **[DDI 417](https://www.isobus.net/isobus/dal/DDI/417)** (0x01A1): Actual Seed Skip Percentage
- **[DDI 419](https://www.isobus.net/isobus/dal/DDI/419)** (0x01A3): Actual Seed Multiple Percentage

**Example:**
```
0x2D, 0x7E, 0x05, 0x64, 0x00, 0x0A, 0x05, 0xF5
```
- Row 5
- Population = 100 (0x0064)
- Skip = 10% (0x0A)
- Multiple = 5% (0x05)
- CRC = 0x2D + 0x7E + 0x05 + 0x64 + 0x00 + 0x0A + 0x05 = 0xF5 (245)

### PGN 32200 - Extended Seeder Data

Additional seeder data format (handled but not actively used in current implementation).

**PGN:** 32200

## Chart Display

### Seeder Mode
- **Green bars**: Show seed rate (0-255) for each row
- **White/Black line**: Average rate across all active rows

### Planter Mode
- **Green (bottom)**: Normal seed population
- **Orange (middle)**: Multiple seeds (doubles)
- **Red (top)**: Skipped/Missed seeds
- **White/Black line**: Average total population across all active rows

The stacked bar height represents total population, with color breakdown showing seed quality distribution.

## Usage

### Starting the Application

1. Launch BlockageMonitor.exe
2. Click the **cogwheel icon** (⚙️) to access settings menu:
   - **Sensors**: Configure row count and enable/disable individual rows
   - **Modules**: Configure module assignments
   - **Mode**: Toggle between Seeder and Planter modes
   - **Transparent**: Enable transparent background

### Row Configuration

1. Click **cogwheel** → **Sensors**
2. Set **Row Count** to match your implement (e.g., 16, 17, 24)
3. Enable/disable specific rows as needed
4. Click **Save**

### Mode Selection

- **Seeder Mode**: For air seeders, grain drills (PGN 32100)
- **Planter Mode**: For precision planters with skip/double monitoring (PGN 32301)

Toggle modes via **cogwheel** menu → **Mode**.

## Simulator

A UDP simulator is included for testing without hardware.

### Simulator Usage

1. Launch BlockageSimulator.exe
2. Select network interface from dropdown
3. Click **Seeder Mode** or **Planter Mode** to start simulation
4. Click **Stop** to stop simulation

### Simulator Data Patterns

**Seeder Mode:**
- Rows 5-6: Low rate (20) - simulates partial blockage
- Row 12: Zero rate - simulates complete blockage
- Other rows: Normal rate (80 ± variation)

**Planter Mode:**
- Rows 3-4: Skip issues (15% skip)
- Rows 8-10: Double seed issues (20% multiple)
- Row 14: Both skip (10%) and multiple (12%)
- Other rows: Normal population

## Technical Details

### Module System

Rows are organized into modules for scalability:
- Default: 16 modules max
- Rows per module: Configurable (default 16)
- Module 0 StartRow is always 1 (1-based indexing)

### Data Flow

```
Hardware/Simulator → UDP Port → PGN Parser → SeedRow Data → Chart Update
```

### File Locations

- **Settings**: Stored in `%AppData%\BlockageMonitor\`
- **Logs**: Error logs saved to application directory

## Building

### Requirements

- .NET Framework 4.7.2 or later
- Visual Studio 2019+ or MSBuild

### Build Commands

```powershell
# Using MSBuild (recommended for .NET Framework projects)
msbuild BlockageMonitor.sln /p:Configuration=Release

# Simulator (SDK-style project)
dotnet build Simulator\BlockageSimulator\BlockageSimulator.csproj
```

## Troubleshooting

### Only 2 rows showing

1. Click **cogwheel** → **Sensors**
2. Verify **Row Count** matches your implement
3. Click **Save** and restart

### No data received

1. Verify correct mode (Seeder/Planter)
2. Check UDP ports (25600 for Seeder, 25800 for Planter)
3. Verify network interface/subnet matches sender
4. Check Windows Firewall settings

### Chart not updating

1. Verify rows are enabled in Sensors menu
2. Check that modules are properly configured
3. Verify CRC calculation in sender

## License

Part of the AgOpenGPS project.
