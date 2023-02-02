//
// Copyright (c) 2023 Jon Lamb (lamb.jon.io@gmail.com)
// Copyright (c) 2010-2020 Antmicro
//
//  This file is licensed under the MIT License.
//  Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Linq;
using System.Collections.Generic;
using Antmicro.Renode.Peripherals.Sensor;
using Antmicro.Renode.Peripherals.I2C;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Utilities;
using Antmicro.Renode.Time;

// See the datasheet: https://www.analog.com/media/en/technical-documentation/data-sheets/DS3231.pdf

// TODO something like
// https://github.com/renode/renode-infrastructure/blob/master/src/Emulator/Peripherals/Peripherals/Timers/STM32F4_RTC.cs
//
// basing this on
// https://github.com/renode/renode-infrastructure/blob/master/src/Emulator/Peripherals/Peripherals/Sensors/TMP103.cs
//
// and
// https://github.com/bitcraze/renode-infrastructure/blob/crazyflie/src/Emulator/Peripherals/Peripherals/Sensors/BMI088_Gyroscope.cs
namespace Antmicro.Renode.Peripherals.Sensors
{
    public class DS3231 : II2CPeripheral, IProvidesRegisterCollection<ByteRegisterCollection>, ISensor
    {
        public DS3231(Machine machine, long wakeupTimerFrequency = DefaultWakeupTimerFrequency)
        {
            RegistersCollection = new ByteRegisterCollection(this);
            DefineRegisters();

            // TODO square wave int
            //Int3 = new GPIO();

            ticker = new LimitTimer(
                    machine.ClockSource, wakeupTimerFrequency,
                    this, "tick", DefaultSynchronuousPrescaler + 1,
                    direction: Direction.Ascending, eventEnabled: true, enabled: true,
                    divider: DefaultAsynchronuousPrescaler + 1);
            ticker.LimitReached += UpdateRtcDateTime;

            dateTime = DateTimeWithCustomWeekday.FromDateTime(new DateTime(2020, 1, 1));

            Reset();
        }

        public void Reset()
        {
            RegistersCollection.Reset();
            registerAddress = null;

            osc_enabled = true;
            pm_20hr = false;
            h12_24 = false;
            century.Value = false;

            // TODO - use dateTime conversions instead
            dayOfWeek = 1;
            dayOfMonth = 1;
            year = 0;

            ticker.Reset();

            this.Log(LogLevel.Noisy, "Reset registers");
        }

        public void Write(byte[] data)
        {
            if(data.Length == 0)
            {
                this.Log(LogLevel.Warning, "Unexpected write with no data");
                return;
            }

            registerAddress = (Registers)data[0];

            if(data.Length > 1)
            {
                foreach(var value in data.Skip(1))
                {
                    this.Log(LogLevel.Noisy, "Writing 0x{0:X} to register {1} (0x{1:X})", value, registerAddress);
                    RegistersCollection.Write((byte)registerAddress, value);
                    registerAddress++;
                }
            }
        }

        public byte[] Read(int count)
        {
            if(!registerAddress.HasValue)
            {
                this.Log(LogLevel.Error, "Trying to read without setting address");
                return new byte[] {};
            }

            // Return first 8 bytes
            // If register address is 0x00 (Seconds), return the first 7 datetime registers
            //var result = new byte[count];
            //for(var i = 0; i < count; ++i)
            var result = new byte[registerAddress == Registers.Seconds ? 7 : 1];
            for(var i = 0; i < result.Length; ++i)
            {
                result[i] = RegistersCollection.Read((byte)((int)registerAddress + i));
                this.Log(LogLevel.Noisy, "Read value 0x{0:X} from register {1} (0x{1:X})", result[i],
                        (Registers)((int)registerAddress + i));
            }
            return result;
        }

        public void FinishTransmission()
        {
            registerAddress = null;
        }

        private void UpdateRtcDateTime()
        {
            if(osc_enabled)
            {
                dateTime.AddSeconds(1);
                if(dateTime.Hour >= 20)
                {
                    pm_20hr = true;
                }
                else
                {
                    pm_20hr = false;
                }
                this.Log(LogLevel.Noisy, "DateTime = {0}:{1}:{2} pm={3} 12/24={4}", dateTime.Hour, dateTime.Minute, dateTime.Second,
                        pm_20hr,
                        h12_24);
            }
        }

        public ByteRegisterCollection RegistersCollection { get; }

        // TODO
        // - define rw vs ro vs wo
        // - Status has bits that are write-0-to-clear
        // - initial PoR values
        // - replace WithTaggedFlag with real handlers WithFlag/etc
        // - conversion stuff for the packed BCD format
        private void DefineRegisters()
        {
            Registers.Seconds.Define(this)
                .WithValueField(0, 7, name: "Seconds",
                    valueProviderCallback: _ => decimal_to_packed_bcd((uint) dateTime.Second),
                    writeCallback: (_, value) => { dateTime.Second = (int) packed_bcd_to_decimal(value); }
                )
            ;
            Registers.Minutes.Define(this)
                .WithValueField(0, 7, name: "Minutes",
                    valueProviderCallback: _ => decimal_to_packed_bcd((uint) dateTime.Minute),
                    writeCallback: (_, value) => { dateTime.Minute = (int) packed_bcd_to_decimal(value); }
                )
            ;
            Registers.Hours.Define(this)
                .WithValueField(0, 5, name: "Hour",
                    valueProviderCallback: _ => decimal_to_packed_bcd((uint) dateTime.Hour),
                    writeCallback: (_, value) => { dateTime.Hour = (int) packed_bcd_to_decimal(value); }
                )
                // NOTE: this is part of the Hour field in 24 hour mode
                // pm_20hr
                .WithFlag(5, name: "AM_PM__20_HR",
                    changeCallback: (_, value) =>
                    {
                        // TODO
                        pm_20hr = value;
                        this.Log(LogLevel.Warning, "Writing to AM/PM not yet supported");
                    },
                    valueProviderCallback: _ => pm_20hr)
                .WithFlag(6, name: "H12_24",
                    changeCallback: (_, value) =>
                    {
                        // TODO
                        h12_24 = value;
                        if(!value)
                        {
                            this.Log(LogLevel.Warning, "12 hour mode not supported");
                        }
                    },
                    valueProviderCallback: _ => h12_24)
            ;
            // TODO
            Registers.DoW.Define(this)
                .WithValueField(0, 3, name: "DoW",
                    valueProviderCallback: _ => dayOfWeek,
                    writeCallback: (_, value) => dayOfWeek = value
                )
            ;
            // TODO
            Registers.DoM.Define(this)
                .WithValueField(0, 6, name: "DoM",
                    valueProviderCallback: _ => decimal_to_packed_bcd(dayOfMonth),
                    writeCallback: (_, value) => dayOfMonth = packed_bcd_to_decimal(value)
                )
            ;
            Registers.Month.Define(this)
                .WithValueField(0, 5, name: "Month",
                    valueProviderCallback: _ => decimal_to_packed_bcd((uint) dateTime.Month),
                    writeCallback: (_, value) => { dateTime.Month = (int) packed_bcd_to_decimal(value); }
                )
                .WithFlag(7, out century, name: "Century")
            ;
            Registers.Year.Define(this)
                // century in Month reg, 1 == 2100 , 0 == 2000, + year
                .WithValueField(0, 8, name: "Year",
                    valueProviderCallback: _ =>
                    {
                        var year = dateTime.Year;
                        if(century.Value)
                        {
                            return decimal_to_packed_bcd((uint) (year - 2100));
                        }
                        else
                        {
                            return decimal_to_packed_bcd((uint) (year - 2000));
                        }
                    },
                    writeCallback: (_, value) =>
                    {
                        var year = (uint) 2000;
                        if(century.Value)
                        {
                            year += 100;
                            century.Value = true;
                        }
                        year += packed_bcd_to_decimal(value);
                        dateTime.Year = (int) year;
                    }
                )
            ;
            Registers.Control.Define(this, 0b00011100)
                .WithTaggedFlag("A1IE", 0)
                .WithTaggedFlag("A2IE", 1)
                .WithTaggedFlag("INTCN", 2)
                .WithTaggedFlag("RS1", 3)
                .WithTaggedFlag("RS2", 4)
                .WithTaggedFlag("CONV", 5)
                .WithTaggedFlag("BBSQW", 6)
                .WithFlag(7, name: "EOSC",
                    changeCallback: (_, value) =>
                    {
                        ticker.Enabled = !value;
                        osc_enabled = !value;

                        this.Log(LogLevel.Noisy, "EOSC set to {}", !value);
                    })
            ;
            Registers.Status.Define(this, 0b10001000)
                .WithTaggedFlag("A1F", 0)
                .WithTaggedFlag("A2F", 1)
                .WithTaggedFlag("BSY", 2)
                .WithTaggedFlag("EN32kHZ", 3)
                .WithTaggedFlag("OSF", 7)
            ;
        }

        private uint decimal_to_packed_bcd(uint dec)
        {
            return ((dec / 10) << 4) | (dec % 10);
        }

        private uint packed_bcd_to_decimal(uint bcd)
        {
            bcd = bcd & 0xFF;
            return (uint) ((bcd >> 4) * 10 + (bcd & 0xF));
        }

        // TODO make enums for binary/etc types
        private bool osc_enabled;
        private bool pm_20hr; // 0 == AM, 1 == PM
        private bool h12_24; // 0 == 24h, 1 == 12h
        private IFlagRegisterField century;

        // TODO - use dateTime conversions instead
        private uint dayOfWeek; // 1-7
        private uint dayOfMonth; // 1-31
        private uint year; // 0-99

        private Registers? registerAddress;

        private readonly LimitTimer ticker;
        private DateTimeWithCustomWeekday dateTime;
        private const long DefaultWakeupTimerFrequency = 32768;
        private const int DefaultSynchronuousPrescaler = 0xFF;
        private const int DefaultAsynchronuousPrescaler = 0x7F;

        private enum Registers : byte
        {
            Seconds = 0x00,
            Minutes = 0x01,
            Hours = 0x02,
            DoW = 0x03,
            DoM = 0x04,
            Month = 0x05,
            Year = 0x06,
            Alarm1Seconds = 0x07,
            Alarm2Seconds = 0x0B,
            Control = 0x0E,
            Status = 0x0F,
            AgingOffset = 0x10,
            TempMsb = 0x11,
            TempConv = 0x13,
        }
    }
}
