# Survey feature finder (Dechante, Linear DIU-64)

Runs on the team server as the `ewarp-classifier` service, installed by `.github/workflows/deploy.yml`.
Every 30 s it reads new rows in `responses` (likes / dislikes / wants plus every written answer in the survey, each sent with its question; 2026-11-03), hides names / contract numbers / money / emails / phones,
sends only the hidden text to Gemini 3.5 Flash-Lite (Google AI, paid tier: not used for training; JSON schema enforced),
and writes `features`, `mentions` and `vendor_offerings` back. Nothing is run by hand. A response that already has
mentions is skipped unless someone clicks Reread.
2026-11-27: switched from DeepSeek via Experiential, which had answered every pass since 11-05 with 403 (zero data
retention moved to its Pro plan). Test on the 65 real masked answers: 53 mentions, 11.3 s.

Server secrets (GitHub repo secrets, never in this public repo):
- `CLASSIFIER_GEMINI_KEY`: the Google AI (Gemini) API key
- `CLASSIFIER_DATABASE_URL`: the app database, `postgresql://...@127.0.0.1:5432/EWARP_TOOL`
- `CLASSIFIER_SENSITIVE_TERMS`: JSON list of real names to hide, shape of `sensitive_terms.example.json`

No `CLASSIFIER_GEMINI_KEY` secret = the deploy stops the classifier and the app deploys as before.
Logs: `sudo journalctl -u ewarp-classifier -f`. What was sent to the AI: `/var/lib/ewarp-classifier/backend_sent_to_ai.json`.
