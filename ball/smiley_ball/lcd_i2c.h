// Tiny driver for a 16x2 character LCD (HD44780) with a PCF8574 I2C "backpack".
// No extra library needed. Finds the backpack's address by itself (usually 0x27 or 0x3F).
//
// Backpack pin map (the common kind): P0=RS  P1=RW  P2=EN  P3=backlight  P4..P7=D4..D7

#pragma once
#include <Arduino.h>
#include <Wire.h>

class LcdI2C {
public:
  // Returns true if a display answered.
  bool begin() {
    Wire.begin();
    addr_ = findAddress();
    if (!addr_) return false;

    delay(50);                        // let the LCD power up
    expanderWrite(0);
    delay(5);
    // Switch to 4-bit mode (the "magic" HD44780 start-up sequence).
    write4(0x30); delay(5);
    write4(0x30); delayMicroseconds(200);
    write4(0x30); delayMicroseconds(200);
    write4(0x20); delayMicroseconds(200);
    command(0x28);                    // 4-bit, 2 lines, 5x8 font
    command(0x0C);                    // display on, no cursor
    command(0x06);                    // move right after each character
    clear();
    return true;
  }

  bool ok() const { return addr_ != 0; }
  uint8_t address() const { return addr_; }

  void clear() { command(0x01); delay(2); }

  void setCursor(uint8_t col, uint8_t row) { command(0x80 | (col + (row ? 0x40 : 0x00))); }

  // Characters 0-7 can be custom 5x8 drawings.
  void createChar(uint8_t slot, const uint8_t rows[8]) {
    command(0x40 | ((slot & 7) << 3));
    for (int i = 0; i < 8; i++) data(rows[i]);
  }

  void write(uint8_t c) { data(c); }

  void print(const char *s) { while (*s) data((uint8_t)*s++); }

  void backlight(bool on) { light_ = on ? 0x08 : 0x00; expanderWrite(0); }

private:
  uint8_t addr_ = 0;
  uint8_t light_ = 0x08;

  static uint8_t findAddress() {
    // Most common first, then every other PCF8574 / PCF8574A address.
    const uint8_t likely[] = {0x27, 0x3F};
    for (uint8_t a : likely) if (probe(a)) return a;
    for (uint8_t a = 0x20; a <= 0x27; a++) if (probe(a)) return a;
    for (uint8_t a = 0x38; a <= 0x3F; a++) if (probe(a)) return a;
    return 0;
  }

  static bool probe(uint8_t a) {
    Wire.beginTransmission(a);
    return Wire.endTransmission() == 0;
  }

  void expanderWrite(uint8_t v) {
    Wire.beginTransmission(addr_);
    Wire.write(v | light_);
    Wire.endTransmission();
  }

  void pulse(uint8_t v) {
    expanderWrite(v | 0x04);          // EN high
    delayMicroseconds(1);
    expanderWrite(v & ~0x04);         // EN low
    delayMicroseconds(50);
  }

  void write4(uint8_t v) { expanderWrite(v); pulse(v); }

  void send(uint8_t value, uint8_t rs) {
    write4((value & 0xF0) | rs);
    write4(((value << 4) & 0xF0) | rs);
  }

  void command(uint8_t c) { if (addr_) send(c, 0); }
  void data(uint8_t d) { if (addr_) send(d, 0x01); }
};
