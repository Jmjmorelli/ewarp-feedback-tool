# Survey feature finder (Dechante, Linear DIU-64)

Runs on the team server as the `ewarp-classifier` service, installed by `.github/workflows/deploy.yml`.
Every 30 s it reads new rows in `responses` (likes / dislikes / wants plus every written answer in the survey, each sent with its question; 2026-11-03), hides names / contract numbers / money / emails / phones,
sends only the hidden text to DeepSeek V4.1 Flash (Experiential, free, zero-data-retention providers only,
thinking off), and writes `features`, `mentions` and `vendor_offerings` back. Nothing is run by hand.
Test on the 32 fake answers (2026-11-03): 77 and 78 of 79 right, 0 of 108 planted secrets sent, $0.00, about 18 s.

Server secrets (GitHub repo secrets, never in this public repo):
- `CLASSIFIER_EXPERIENTIAL_KEY`: the Experiential API key
- `CLASSIFIER_DATABASE_URL`: the app database, `postgresql://...@127.0.0.1:5432/EWARP_TOOL`
- `CLASSIFIER_SENSITIVE_TERMS`: JSON list of real names to hide, shape of `sensitive_terms.example.json`

No `CLASSIFIER_EXPERIENTIAL_KEY` secret = the deploy skips the classifier and the app deploys as before.
Logs: `sudo journalctl -u ewarp-classifier -f`. What was sent to the AI: `/var/lib/ewarp-classifier/backend_sent_to_ai.json`.
