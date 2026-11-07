// Smiley Ball - microcontroller side.
//
// Reads the button on D2 and shows a smiley + status on a 16x2 I2C LCD.
// The Linux side (recorder.py) asks for button events over the Bridge and
// tells us which face to show.
//
//   Short press        -> event 1 (start / stop recording)
//   Hold for 2 seconds -> event 2 (bookmark "this part is important")
//
// Wiring (see wiring/wiring.svg):
//   Button: one leg to D2, the diagonally opposite leg to GND (internal pull-up, no resistor).
//   LCD backpack: GND->GND, VCC->5V, SDA->SDA, SCL->SCL (the two pins next to AREF).

#include <Arduino.h>
#include <Arduino_RouterBridge.h>
#include "lcd_i2c.h"

const int BUTTON_PIN = 2;
const uint32_t DEBOUNCE_MS = 30;
const uint32_t HOLD_MS = 2000;

enum Face { IDLE = 0, RECORDING = 1, WINK = 2, BUSY = 3, FROWN = 4, FLAT = 5, FACE_COUNT };

// Custom 5x8 characters (1 = pixel on). Slots 0-6 on the LCD.
enum Glyph { G_SMILE = 0, G_WINK, G_OOH, G_SAD, G_FLAT, G_STAR, G_DOT };
const uint8_t GLYPHS[7][8] = {
  {0b00000, 0b01010, 0b01010, 0b00000, 0b10001, 0b01110, 0b00000, 0b00000},  // smile
  {0b00000, 0b00010, 0b11010, 0b00000, 0b10001, 0b01110, 0b00000, 0b00000},  // wink
  {0b00000, 0b01010, 0b01010, 0b00000, 0b00100, 0b01010, 0b00100, 0b00000},  // "ooh"
  {0b00000, 0b01010, 0b01010, 0b00000, 0b01110, 0b10001, 0b00000, 0b00000},  // sad
  {0b00000, 0b01010, 0b01010, 0b00000, 0b00000, 0b11111, 0b00000, 0b00000},  // flat
  {0b00100, 0b00100, 0b11111, 0b01110, 0b01010, 0b10001, 0b00000, 0b00000},  // star
  {0b00000, 0b01110, 0b11111, 0b11111, 0b11111, 0b01110, 0b00000, 0b00000},  // record dot
};

LcdI2C lcd;

volatile int face = IDLE;       // set by Linux through set_face()
uint32_t recStart = 0;          // when recording started (for the timer)
int bookmarks = 0;              // bookmarks in this recording
uint32_t flashUntil = 0;        // show a short message after a hold
bool flashWasBookmark = false;

// Small queue of button events for the Linux side to collect.
const int QUEUE_SIZE = 8;
volatile int queue[QUEUE_SIZE];
volatile int q_head = 0, q_tail = 0;

void pushEvent(int e) {
  int next = (q_head + 1) % QUEUE_SIZE;
  if (next == q_tail) return;   // full - drop it
  queue[q_head] = e;
  q_head = next;
}

// Bridge: returns 0 (nothing), 1 (short press) or 2 (hold).
int onBtnEvent() {
  if (q_tail == q_head) return 0;
  int e = queue[q_tail];
  q_tail = (q_tail + 1) % QUEUE_SIZE;
  return e;
}

// Bridge: choose the face to show.
void onSetFace(int f) {
  if (f < 0 || f >= FACE_COUNT || f == WINK) return;
  if (f == RECORDING && face != RECORDING) { recStart = millis(); bookmarks = 0; }
  face = f;
}

// ---------------------------------------------------------------- button
void readButton() {
  static bool stable = HIGH, last = HIGH;
  static uint32_t changed = 0, pressedAt = 0;
  static bool holdSent = false;

  bool now = digitalRead(BUTTON_PIN);
  uint32_t t = millis();
  if (now != last) { last = now; changed = t; }
  if (t - changed < DEBOUNCE_MS) return;

  if (now != stable) {
    stable = now;
    if (stable == LOW) {               // pressed
      pressedAt = t;
      holdSent = false;
    } else if (!holdSent) {            // released before 2 s
      pushEvent(1);
    }
  }
  if (stable == LOW && !holdSent && t - pressedAt >= HOLD_MS) {
    holdSent = true;
    pushEvent(2);
    flashWasBookmark = (face == RECORDING);
    if (flashWasBookmark) bookmarks++;
    flashUntil = t + 1500;             // react right away, before Linux answers
  }
}

// ---------------------------------------------------------------- screen
char shown[2][17];                     // what's on the LCD now, to avoid flicker

// Writes one line: an optional glyph, then text, padded to 16 characters,
// and optionally a special character in the last column.
void drawLine(uint8_t row, int glyph, const char *text, int lastChar = -1) {
  char buf[17];
  int n = 0;
  if (glyph >= 0) { buf[n++] = (char)(glyph == 0 ? 8 : glyph); buf[n++] = ' '; }   // slot 0 is also code 8
  for (const char *p = text; *p && n < 16; p++) buf[n++] = *p;
  while (n < 16) buf[n++] = ' ';
  if (lastChar >= 0) buf[15] = (char)lastChar;
  buf[16] = 0;
  if (memcmp(buf, shown[row], 16) == 0) return;
  memcpy(shown[row], buf, 17);
  lcd.setCursor(0, row);
  for (int i = 0; i < 16; i++) lcd.write((uint8_t)buf[i]);
}

void timeText(char *out, uint32_t ms) {
  uint32_t s = ms / 1000;
  if (s >= 3600) snprintf(out, 10, "%lu:%02lu:%02lu", s / 3600, (s / 60) % 60, s % 60);
  else snprintf(out, 10, "%02lu:%02lu", s / 60, s % 60);
}

void drawScreen() {
  uint32_t t = millis();
  char line[24], clock[12];
  timeText(clock, t - recStart);

  if (t < flashUntil) {
    if (flashWasBookmark) {
      drawLine(0, G_WINK, "Bookmarked!");
      snprintf(line, sizeof line, "at %s", clock);
      drawLine(1, -1, line);
    } else {
      drawLine(0, G_SMILE, "Not recording");
      drawLine(1, -1, "Tap to start");
    }
    return;
  }

  switch (face) {
    case RECORDING: {
      bool on = (t / 500) % 2 == 0;     // blinking dot in the top-right corner
      drawLine(0, G_SMILE, "Recording", on ? G_DOT : ' ');
      snprintf(line, sizeof line, "%-8s  %c x%d", clock, (char)G_STAR, bookmarks);
      drawLine(1, -1, line);
      break;
    }
    case BUSY:  drawLine(0, G_OOH,  "Syncing...");    drawLine(1, -1, "Sending to PC"); break;
    case FROWN: drawLine(0, G_SAD,  "No microphone"); drawLine(1, -1, "Plug in Go Mic");    break;
    case FLAT:  drawLine(0, G_FLAT, "Disk is full");  drawLine(1, -1, "Sync w/ laptop");    break;
    default:    drawLine(0, G_SMILE, "Smiley Ball");  drawLine(1, -1, "Tap to record");     break;
  }
}

void startLcd() {
  if (!lcd.begin()) return;
  for (int i = 0; i < 7; i++) lcd.createChar(i, GLYPHS[i]);
  memset(shown, 0, sizeof shown);
}

// ---------------------------------------------------------------- main
void setup() {
  pinMode(BUTTON_PIN, INPUT_PULLUP);
  startLcd();
  Bridge.begin();
  Bridge.provide("btn_event", onBtnEvent);
  Bridge.provide("set_face", onSetFace);
}

void loop() {
  readButton();

  static uint32_t lastDraw = 0, lastRetry = 0;
  uint32_t t = millis();
  if (!lcd.ok()) {
    if (t - lastRetry > 3000) { lastRetry = t; startLcd(); }   // LCD plugged in later
  } else if (t - lastDraw >= 100) {
    lastDraw = t;
    drawScreen();
  }
  delay(5);
}
