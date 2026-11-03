// Prints the SQL to add a user (e.g. the first admin), so the password never leaves your computer: only its hash
// goes in the database. Run it, then paste the printed INSERT into pgAdmin's Query Tool.
//
//     node Data/Migrations/create-user.mjs
//
// The hash is the format ASP.NET Core's PasswordHasher reads (V3: PBKDF2-HMAC-SHA512, 100,000 iterations,
// 16-byte salt, 32-byte key), so UserRepository.CheckPasswordAsync accepts it.

import { pbkdf2Sync, randomBytes, randomUUID } from "node:crypto";
import { createInterface } from "node:readline";

function ask(question, hidden = false) {
  return new Promise((resolve) => {
    const rl = createInterface({ input: process.stdin, output: process.stdout, terminal: true });
    if (hidden) {
      // Don't echo the password as it's typed.
      rl._writeToOutput = (text) => rl.output.write(text.includes(question) ? text : "");
    }
    rl.question(question, (answer) => {
      rl.close();
      if (hidden) process.stdout.write("\n");
      resolve(answer);
    });
  });
}

function hashPassword(password) {
  const salt = randomBytes(16);
  const subkey = pbkdf2Sync(password, salt, 100_000, 32, "sha512");
  const header = Buffer.alloc(13);
  header.writeUInt8(0x01, 0); // format marker: V3
  header.writeUInt32BE(2, 1); // PRF: HMACSHA512
  header.writeUInt32BE(100_000, 5); // iterations
  header.writeUInt32BE(salt.length, 9);
  return Buffer.concat([header, salt, subkey]).toString("base64");
}

const sql = (value) => `'${value.replace(/'/g, "''")}'`;

const email = (await ask("Email: ")).trim().toLowerCase();
const name = (await ask("Name: ")).trim().replace(/\s+/g, " ");
const role = (await ask("Role (admin or viewer) [admin]: ")).trim().toLowerCase() || "admin";
const password = await ask("Password (8+ characters, not shown): ", true);
const again = await ask("Password again: ", true);

if (!email.includes("@")) throw new Error("Enter a valid email.");
if (!name) throw new Error("Enter a name.");
if (role !== "admin" && role !== "viewer") throw new Error("Role must be admin or viewer.");
if (password.length < 8 || password.length > 128) throw new Error("Password must be 8 to 128 characters.");
if (password !== again) throw new Error("The passwords don't match.");

const id = "usr_" + randomUUID().replace(/-/g, "");
console.log("\nPaste this into pgAdmin's Query Tool and run it:\n");
console.log(
  `insert into users (id, email, name, password_hash, role) values (${sql(id)}, ${sql(email)}, ${sql(name)}, ${sql(hashPassword(password))}, ${sql(role)});\n`,
);
