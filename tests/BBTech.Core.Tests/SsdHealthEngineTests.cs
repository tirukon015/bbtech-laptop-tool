using System;
using System.Collections.Generic;
using BBTech.Core.Models;
using BBTech.Core.Parsing;
using BBTech.Core.Storage;
using Xunit;

namespace BBTech.Core.Tests
{
    public class SsdHealthEngineTests
    {
        [Theory]
        [InlineData(100, SsdHealthStatus.Optimal)]
        [InlineData(95, SsdHealthStatus.Optimal)]
        [InlineData(90, SsdHealthStatus.Optimal)]
        [InlineData(89, SsdHealthStatus.Good)]
        [InlineData(80, SsdHealthStatus.Good)]
        [InlineData(79, SsdHealthStatus.Warning)]
        [InlineData(50, SsdHealthStatus.Warning)]
        [InlineData(49, SsdHealthStatus.Critical)]
        [InlineData(0, SsdHealthStatus.Critical)]
        public void EvaluateStatus_CategorizesHealthPercentagesCorrectly(int health, SsdHealthStatus expected)
        {
            var status = PhysicalSsdHealthResult.EvaluateStatus(health);
            Assert.Equal(expected, status);
        }

        [Fact]
        public void EvaluateStatus_WindowsStatusOverridesWhenCritical()
        {
            // Even if health percent was not populated, Windows Unhealthy should be Critical
            var status1 = PhysicalSsdHealthResult.EvaluateStatus(null, "Unhealthy");
            Assert.Equal(SsdHealthStatus.Critical, status1);

            var status2 = PhysicalSsdHealthResult.EvaluateStatus(null, "Warning");
            Assert.Equal(SsdHealthStatus.Warning, status2);

            var status3 = PhysicalSsdHealthResult.EvaluateStatus(null, "Healthy");
            Assert.Equal(SsdHealthStatus.Good, status3);
        }

        [Fact]
        public void SsdHealthMatcher_CorrelatesHdSentinelDisks_ToDiscoveredStorageDevices()
        {
            var devices = new List<StorageDevice>
            {
                new StorageDevice
                {
                    DeviceId = @"\\.\PHYSICALDRIVE0",
                    Model = "Samsung SSD 980 PRO 1TB",
                    BusType = "NVMe",
                    WindowsHealth = "Healthy",
                    CapacityLabel = "1.0 TB",
                    CapacityBytes = 1000204886016,
                    IsExternal = false
                }
            };

            var hdsDisks = new List<HdSentinelDisk>
            {
                new HdSentinelDisk
                {
                    Model = "Samsung SSD 980 PRO 1TB",
                    HealthPercent = 99,
                    PerformancePercent = 100,
                    Temperature = "38 °C",
                    PowerOnTime = "124 days, 6 hours",
                    Description = "The status of the solid state disk is PERFECT. No bad or weak sectors were found.",
                    Interface = "NVMe",
                    TotalSize = "1000 GB"
                }
            };

            var result = SsdHealthMatcher.Correlate(devices, hdsDisks, "HD Sentinel Pro (v6.30)");

            Assert.True(result.Success);
            Assert.Single(result.Disks);
            var d = result.Disks[0];

            Assert.Equal("Samsung SSD 980 PRO 1TB", d.Model);
            Assert.Equal(99, d.HealthPercent);
            Assert.Equal(100, d.PerformancePercent);
            Assert.Equal("38 °C", d.Temperature);
            Assert.Equal("124 days, 6 hours", d.PowerOnTime);
            Assert.Equal(SsdHealthStatus.Optimal, d.Status);
            Assert.Equal("OPTIMAL", d.StatusLabel);
            Assert.Equal("HD Sentinel Pro (v6.30)", d.DataSource);
            Assert.Contains("PERFECT", d.DiagnosticDetails);
            Assert.True(result.AllHealthy);
            Assert.False(result.AnyBadDrives);

            // Verify synchronization to underlying StorageDevice model
            Assert.Equal(99, devices[0].HealthPercent);
            Assert.Equal(100, devices[0].PerformancePercent);
            Assert.Equal("OPTIMAL", devices[0].HealthAssessment);
        }

        [Fact]
        public void SsdHealthMatcher_FallsBackToNVMeWear_WhenNoHdSentinelAvailable()
        {
            var devices = new List<StorageDevice>
            {
                new StorageDevice
                {
                    DeviceId = @"\\.\PHYSICALDRIVE0",
                    Model = "KIOXIA KBG40ZNV512G",
                    BusType = "NVMe",
                    WindowsHealth = "Healthy",
                    CapacityLabel = "512 GB",
                    WearPercentUsed = 4, // 4% worn = 96% health
                    PowerOnHours = 3120,
                    TemperatureC = 41,
                    IsExternal = false
                }
            };

            // No HD Sentinel report available
            var result = SsdHealthMatcher.Correlate(devices, null, "Windows Diagnostic Fallback");

            Assert.True(result.Success);
            Assert.Single(result.Disks);
            var d = result.Disks[0];

            Assert.Equal("KIOXIA KBG40ZNV512G", d.Model);
            // Native fallback calculates health percentage from NVMe wear (100 - 4% wear = 96%)
            Assert.Equal(96, d.HealthPercent);
            Assert.Equal(SsdHealthStatus.Good, d.Status);
            Assert.Equal("GOOD", d.StatusLabel);
            Assert.Equal("Windows Storage Reliability API (NVMe)", d.DataSource);
            Assert.Equal(4, d.WearPercentUsed);
            Assert.Equal(3120, d.PowerOnHours);
            Assert.Equal("41 °C", d.Temperature);
            Assert.Contains("NVMe health: 96%", d.DiagnosticDetails);
            Assert.Contains("wear consumed: 4%", d.DiagnosticDetails);
            Assert.True(result.AllHealthy);
        }

        [Fact]
        public void SsdHealthMatcher_FlagsCriticalDrive_AndUpdatesOverallStatus()
        {
            var devices = new List<StorageDevice>
            {
                new StorageDevice
                {
                    DeviceId = @"\\.\PHYSICALDRIVE0",
                    Model = "Kingston A400 240GB",
                    BusType = "SATA",
                    WindowsHealth = "Warning",
                    IsExternal = false
                }
            };

            var hdsDisks = new List<HdSentinelDisk>
            {
                new HdSentinelDisk
                {
                    Model = "Kingston A400 240GB",
                    HealthPercent = 18,
                    PerformancePercent = 40,
                    Temperature = "52 °C",
                    PowerOnTime = "940 days",
                    Description = "The drive has critical errors and weak sectors. Immediate backup and replacement required."
                }
            };

            var result = SsdHealthMatcher.Correlate(devices, hdsDisks);

            Assert.True(result.Success);
            Assert.True(result.AnyBadDrives);
            Assert.False(result.AllHealthy);
            Assert.Equal(SsdHealthStatus.Critical, result.OverallStatus);
            Assert.Contains("replacement", result.SummaryMessage.ToLowerInvariant());

            var disk = result.Disks[0];
            Assert.Equal(SsdHealthStatus.Critical, disk.Status);
            Assert.Equal("CRITICAL", disk.StatusLabel);
            Assert.Equal(18, disk.HealthPercent);
        }

        [Fact]
        public void SsdHealthMatcher_ExternalDrivesDoNotCompromiseInternalHealthStatus()
        {
            var devices = new List<StorageDevice>
            {
                new StorageDevice
                {
                    DeviceId = @"\\.\PHYSICALDRIVE0",
                    Model = "Micron 2450 NVMe 512GB",
                    BusType = "NVMe",
                    WindowsHealth = "Healthy",
                    IsExternal = false
                },
                new StorageDevice
                {
                    DeviceId = @"\\.\PHYSICALDRIVE1",
                    Model = "SanDisk Ultra USB 3.0",
                    BusType = "USB",
                    WindowsHealth = "Unhealthy", // External USB failing or corrupt
                    IsExternal = true
                }
            };

            var hdsDisks = new List<HdSentinelDisk>
            {
                new HdSentinelDisk
                {
                    Model = "Micron 2450 NVMe 512GB",
                    HealthPercent = 98,
                    PerformancePercent = 100
                }
            };

            var result = SsdHealthMatcher.Correlate(devices, hdsDisks);

            Assert.True(result.Success);
            Assert.Equal(2, result.Disks.Count);
            // Internal drive is healthy, external is ignored for internal failure flag
            Assert.False(result.AnyBadDrives);
            Assert.True(result.AllHealthy);
        }

        [Fact]
        public void SsdHealthMatcher_NullOrEmptyInputs_ProducesGracefulResult()
        {
            var res1 = SsdHealthMatcher.Correlate(null, null);
            Assert.NotNull(res1);
            Assert.True(res1.Success);
            Assert.Empty(res1.Disks);
            Assert.Equal(SsdHealthStatus.Unknown, res1.OverallStatus);

            var res2 = SsdHealthMatcher.Correlate(new List<StorageDevice>(), new List<HdSentinelDisk>());
            Assert.NotNull(res2);
            Assert.True(res2.Success);
            Assert.Empty(res2.Disks);
        }

        [Fact]
        public void SsdHealthMatcher_HonorsPreEnrichedHealthPercent_AndUpdatesDetectionResult()
        {
            var detection = new DetectionResult();
            var devices = new List<StorageDevice>
            {
                new StorageDevice
                {
                    DeviceId = @"\\.\PHYSICALDRIVE0",
                    Model = "Micron MTFDKBA512TFK",
                    SerialNumber = "00A0_7501_3CC4_631F.",
                    BusType = "NVMe",
                    WindowsHealth = "Healthy",
                    HealthPercent = 96,
                    WearPercentUsed = 4,
                    HealthSource = "Windows Native NVMe Protocol IOCTL",
                    TemperatureC = 39,
                    PowerOnHours = 15560,
                    IsExternal = false
                }
            };

            var result = SsdHealthMatcher.Correlate(devices, null, null, detection);

            Assert.True(result.Success);
            Assert.Single(result.Disks);
            var d = result.Disks[0];

            Assert.Equal(96, d.HealthPercent);
            Assert.Equal(SsdHealthStatus.Optimal, d.Status);
            Assert.Equal("OPTIMAL", d.StatusLabel);
            Assert.Equal("Windows Native NVMe Protocol IOCTL", d.DataSource);
            Assert.Equal("96% (OPTIMAL)", detection.SsdHealth);
        }

        [Fact]
        public void SsdHealthMatcher_NeverFabricates100Percent_WhenOnlyBasicOperationalStatusKnown()
        {
            var detection = new DetectionResult();
            var devices = new List<StorageDevice>
            {
                new StorageDevice
                {
                    DeviceId = @"\\.\PHYSICALDRIVE0",
                    Model = "Generic SSD 512GB",
                    BusType = "SATA",
                    WindowsHealth = "Healthy",
                    HealthPercent = null,
                    WearPercentUsed = null,
                    IsExternal = false
                }
            };

            var result = SsdHealthMatcher.Correlate(devices, null, null, detection);

            Assert.True(result.Success);
            Assert.Single(result.Disks);
            var d = result.Disks[0];

            // Must NOT fabricate 100% when actual wear is unknown
            Assert.Null(d.HealthPercent);
            Assert.Equal("Windows Basic Storage API", d.DataSource);
            Assert.DoesNotContain("100%", detection.SsdHealth);
            Assert.Equal("Healthy", detection.SsdHealth);
        }

        [Fact]
        public void HdSentinelDataService_EnrichesFromStaLines_WithRealMicronData()
        {
            var dev = new StorageDevice
            {
                Model = "Micron MTFDKBA512TFK",
                SerialNumber = "00A0_7501_3CC4_631F.",
                DeviceId = @"\\.\PHYSICALDRIVE0",
                BusType = "NVMe"
            };

            var lines = new[]
            {
                "[Sta_Micron MTFDKBA512TFK (488382 MB)_22453CC4631F]",
                "46270=51,39.99,101,96,25,18176247"
            };

            bool ok = HdSentinelDataService.EnrichFromStaLines(lines, dev);

            Assert.True(ok);
            Assert.Equal(96, dev.HealthPercent);
            Assert.Equal(40, dev.TemperatureC);
            Assert.Equal("OPTIMAL", dev.HealthAssessment);
            Assert.Contains("96% health", dev.DiagnosticDetails);
        }

        [Fact]
        public void HdSentinelDataService_EnrichesFromHddLines_WithRealNvmeSmartLog()
        {
            var dev = new StorageDevice
            {
                Model = "Micron MTFDKBA512TFK",
                SerialNumber = "00A0_7501_3CC4_631F.",
                DeviceId = @"\\.\PHYSICALDRIVE0",
                BusType = "NVMe"
            };

            var lines = new[]
            {
                "[46270]",
                "SMART=FF_1200=00_00,FF_1201=00_013E,FF_1203=00_64,FF_1204=00_05,FF_1205=00_04,FF_1220=00_00000000000000000000000002C52F8D,FF_1230=00_000000000000000000000000023801FB,FF_1280=00_00000000000000000000000000003CC8"
            };

            bool ok = HdSentinelDataService.ParseHddLines(lines, dev);

            Assert.True(ok);
            // 4% wear consumed = 96% health
            Assert.Equal(96, dev.HealthPercent);
            Assert.Equal(4, dev.WearPercentUsed);
            Assert.Equal(45, dev.TemperatureC);
            Assert.Equal(15560, dev.PowerOnHours);
            Assert.Equal("OPTIMAL", dev.HealthAssessment);
            Assert.Contains("96% health (4% endurance used)", dev.DiagnosticDetails);
        }
    }
}
