# Survey feature finder (Dechante, Linear DIU-64)

Runs on the team server as the `ewarp-classifier` service, installed by `.github/workflows/deploy.yml`.
Every 30 s it reads new rows in `responses`, hides names / contract numbers / money / emails / phones,
sends only the hidden text to Claude Sonnet 5 (RightCodes), and writes `features`, `mentions` and
`vendor_offerings` back. Nothing is run by hand.

Server secrets (GitHub repo secrets, never in this public repo):
- `CLASSIFIER_RIGHTCODES_KEY`: a separate, capped RightCodes key just for this server
- `CLASSIFIER_DATABASE_URL`: the app database, `postgresql://...@127.0.0.1:5432/EWARP_TOOL`
- `CLASSIFIER_SENSITIVE_TERMS`: JSON list of real names to hide, shape of `sensitive_terms.example.json`

No `CLASSIFIER_RIGHTCODES_KEY` secret = the deploy skips the classifier and the app deploys as before.
Logs: `sudo journalctl -u ewarp-classifier -f`. What was sent to the AI: `/var/lib/ewarp-classifier/backend_sent_to_ai.json`.
