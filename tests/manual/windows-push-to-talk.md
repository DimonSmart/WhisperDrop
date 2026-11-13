# Windows Push-to-Talk acceptance checks

Run these checks on an interactive **Windows x64** desktop with a packaged ParakeetDrop installation and the locally downloaded Parakeet Ultra Q8_0 model. CI compilation and unit tests do not replace these checks.

1. Launch, close main window with X, confirm tray icon remains; Open from the tray must restore the same queue, Exit must terminate and remove temporary microphone WAV files.
2. Focus an ordinary Notepad text field, hold Ctrl+Shift+Space, speak, release. The topmost recording indicator must appear **only after microphone capture starts**, never take focus or receive pointer/keyboard input, and disappear as soon as capture has actually stopped. After recognition, the text must be submitted exactly once. Confirm the queue contains one Push-to-Talk row with independent insertion status.
3. Repeat in a multiline Notepad field, Chrome/Edge web text box and VS Code. Include Unicode Russian, English, Spanish and newlines.
4. While the model is recognizing, switch apps, switch to a second field within the same browser tab, reload the page or close the original window. Verify insertion is skipped, no field is activated, the text remains in the original queue row and Copy works.
5. Try a password field, an elevated process and a target with no reliable UI Automation identity. Ensure no automatic insertion occurs.
6. Make two short dictations rapidly while the first recognizes: two rows and two sequential native recognitions, never overlapping native model calls. A tap with no usable audio must not produce an empty result.
7. With ParakeetDrop's manual recording active, the background shortcut must not start another capture. Stop manual recording and confirm it remains queued but does not automatically recognize.
8. Try changing the shortcut to an occupied Windows shortcut and to another valid shortcut; confirm conflict message and persistence across restart. Toggle dictation from the tray and settings.
9. Lock the session, suspend the computer and deliberately release the shortcut during processing. Verify the microphone never remains indefinitely active and the indicator always matches the actual capture state.
10. With the model missing or microphone permission denied, inspect actionable error status, no hidden listening, and no lost finalized audio.
11. With raw transcription completed but failed target injection, the row must stay copyable. Manually retranscribe it: no automatic second insertion.
12. Check packaging on a clean Windows x64 installation without an independently installed FFmpeg or transcribe.cpp runtime. Verify recognition uses the bundled native runtime and does not invoke any remote ASR service.

Expected status: **Submitted** means input events were submitted, not that the target application confirmed the resulting text; do not treat it as guaranteed insertion.
