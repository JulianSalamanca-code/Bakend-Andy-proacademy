// Verificación estructural de las migraciones SQL.
// No reemplaza a psql: comprueba balance de bloques y delimitadores, que es
// donde fallan los errores de tipeo al escribir plpgsql a mano.
const fs = require('fs');
const path = require('path');

const dir = path.join(__dirname, '..', 'db', 'migrations');
const files = fs.readdirSync(dir).filter((f) => f.endsWith('.sql'));

let problems = 0;

for (const file of files) {
  const raw = fs.readFileSync(path.join(dir, file), 'utf8');

  // Quita comentarios de línea para no contar palabras dentro de notas.
  const code = raw
    .split('\n')
    .map((l) => l.replace(/--.*$/, ''))
    .join('\n');

  // Solo se validan los conteos que no admiten ambigüedad. Contar begin/end
  // en plpgsql no sirve: 'end' cierra bloques, 'if', 'loop' y 'case', así que
  // un desbalance aquí no distingue un error real de un CASE legitimate.
  // El balance de bloques lo confirma psql cuando haya contraseña.
  const dollars = (raw.match(/\$\$/g) || []).length;
  const parenOpen = (code.match(/\(/g) || []).length;
  const parenClose = (code.match(/\)/g) || []).length;

  const issues = [];
  if (dollars % 2 !== 0) issues.push(`$$ sin pareja (${dollars})`);
  if (parenOpen !== parenClose) issues.push(`paréntesis desbalanceados (${parenOpen}/${parenClose})`);

  const beginCount = (code.match(/\bbegin\b/gi) || []).length;
  const endIf = (code.match(/\bend\s+if\b/gi) || []).length;
  const endLoop = (code.match(/\bend\s+loop\b/gi) || []).length;

  // Un 'if' siempre cierra con 'end if'; el imbalance sí es un error real.
  // Ojo: 'case ... then' no lleva 'end if', hay que excluirlo.
  const thenTotal = (code.match(/\bthen\b/gi) || []).length;
  const caseThen = (code.match(/\bcase\b[^;]*?\bthen\b/gi) || []).length;
  const ifThen = thenTotal - caseThen;

  if (ifThen !== endIf) issues.push(`if/then (${ifThen}) != end if (${endIf})`);

  const status = issues.length ? 'REVISAR' : 'OK';
  console.log(
    `${file.padEnd(20)} ${status.padEnd(8)} líneas=${String(raw.split('\n').length).padEnd(5)} $$=${dollars} if=${ifThen} endif=${endIf} loop=${endLoop}`
  );

  for (const issue of issues) {
    console.log(`    -> ${issue}`);
    problems++;
  }

  // Detecta caracteres fuera del rango latino habitual, que suelen ser
  // texto de otro idioma colado en un comentario. La flecha (→) y el guion
  // largo (—) se usan a propósito en comentarios, así que no se marcan.
  const allowedExtra = new Set(['→', '—', '–']);
  const suspicious = [...raw].filter((c) => {
    if (allowedExtra.has(c)) return false;
    const code = c.codePointAt(0);
    const ok =
      code < 128 || // ASCII
      (code >= 0xc0 && code <= 0x24f) || // latín
      (code >= 0x2010 && code <= 0x205e) || // comillas, guiones, espacios
      (code >= 0x2190 && code <= 0x21ff) || // flechas
      code === 0x20ac || // euro
      (code >= 0x00a0 && code <= 0x00ff); // acentuadas extendidas
    return !ok;
  });
  if (suspicious.length) {
    console.log(`    -> ${suspicious.length} caracteres fuera de rango latino`);
    problems++;
  }
}

console.log(problems === 0 ? '\nSin problemas estructurales.' : `\n${problems} problema(s) por revisar.`);