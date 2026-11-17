namespace Waddamburo.Android;

/// <summary>The page a PC's browser opens to send the game's files to the phone (<see cref="UploadServer"/>).</summary>
internal static class UploadPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Waddamburo</title>
<style>
  :root { color-scheme: light dark; --bg: #f6f3ee; --card: #fff; --ink: #1d1b19; --muted: #6b655e; --accent: #d9452b; --line: #e3ddd4; }
  @media (prefers-color-scheme: dark) { :root { --bg: #161412; --card: #211e1b; --ink: #f1ece6; --muted: #a39b91; --line: #34302b; } }
  body { margin: 0; background: var(--bg); color: var(--ink); font: 16px/1.5 system-ui, sans-serif; }
  main { max-width: 640px; margin: 0 auto; padding: 32px 16px; }
  h1 { font-size: 28px; margin: 0 0 4px; }
  p { color: var(--muted); margin: 0 0 24px; }
  section { background: var(--card); border: 1px solid var(--line); border-radius: 12px; padding: 20px; margin-bottom: 16px; }
  h2 { font-size: 18px; margin: 0 0 4px; }
  section p { margin-bottom: 14px; }
  label { display: inline-block; background: var(--accent); color: #fff; padding: 10px 18px; border-radius: 8px; cursor: pointer; font-weight: 600; }
  input { display: none; }
  progress { width: 100%; height: 10px; margin-top: 14px; accent-color: var(--accent); }
  .status { margin-top: 8px; font-variant-numeric: tabular-nums; color: var(--muted); min-height: 1.5em; }
</style>
</head>
<body>
<main>
  <h1>Waddamburo</h1>
  <p>Send the game's files to the phone. Keep this page open until it says done; if the copy stops, choose the folder again and it goes on where it stopped.</p>
  <section>
    <h2>Taiko no Tatsujin Green</h2>
    <p>Choose Green's <b>USRDIR</b> folder (or the folder holding it).</p>
    <label>Choose folder<input type="file" webkitdirectory data-target="green"></label>
    <progress value="0" max="1" hidden></progress>
    <div class="status"></div>
  </section>
  <section>
    <h2>Custom songs</h2>
    <p>Choose a folder of TJA songs: it is added to the phone's Custom Songs.</p>
    <label>Choose folder<input type="file" webkitdirectory data-target="songs"></label>
    <progress value="0" max="1" hidden></progress>
    <div class="status"></div>
  </section>
</main>
<script>
const mb = n => (n / 1048576).toFixed(0) + ' MB';
// The page's own address (http://phone:port/code/): every request carries the code.
const base = '/' + location.pathname.split('/').filter(Boolean)[0] + '/';
for (const input of document.querySelectorAll('input')) {
  input.addEventListener('change', () => send(input));
}
async function send(input) {
  const target = input.dataset.target;
  const section = input.closest('section');
  const bar = section.querySelector('progress');
  const status = section.querySelector('.status');
  // Where each file goes: Green's below USRDIR (data/... only), songs below the chosen folder's name.
  const files = [];
  for (const file of input.files) {
    let path = file.webkitRelativePath.split('/');
    if (target === 'green') {
      path = path.slice(1);
      if (path[0] === 'USRDIR') path = path.slice(1);
      if (path[0] !== 'data') continue;
    }
    files.push({ file, path: path.join('/') });
  }
  if (target === 'green' && files.length === 0) {
    status.textContent = 'No data folder in there: choose Green\'s USRDIR.';
    return;
  }
  status.textContent = 'Checking what the phone has…';
  const have = new Map((await (await fetch(base + 'have?target=' + target)).text())
    .split('\n').filter(Boolean).map(line => line.split('\t')).map(([p, s]) => [p, Number(s)]));
  const queue = files.filter(f => have.get(f.path) !== f.file.size);
  const total = queue.reduce((sum, f) => sum + f.file.size, 0);
  let done = 0, sent = 0, failed = 0;
  const start = Date.now();
  bar.hidden = false; bar.max = Math.max(total, 1); bar.value = 0;
  const show = () => {
    const seconds = (Date.now() - start) / 1000;
    status.textContent = `${done} of ${queue.length} files, ${mb(sent)} of ${mb(total)}`
      + (seconds > 2 ? `, ${mb(sent / seconds)}/s` : '') + (failed ? `, ${failed} retried` : '');
  };
  // Many small files go in one request (Green has ~13,000 charts of ~16 KB: a request each was most of the time);
  // each file is [path length (4 bytes), path (UTF-8), size (8 bytes), contents], little-endian.
  const encoder = new TextEncoder();
  function nextBatch() {
    const batch = [queue.shift()];
    let bytes = batch[0].file.size;
    while (queue.length && batch.length < 200 && bytes + queue[0].file.size <= 16 << 20) {
      const item = queue.shift();
      batch.push(item); bytes += item.file.size;
    }
    return batch;
  }
  function body(batch) {
    const parts = [];
    for (const item of batch) {
      const name = encoder.encode(item.path);
      const header = new DataView(new ArrayBuffer(4 + name.length + 8));
      header.setUint32(0, name.length, true);
      new Uint8Array(header.buffer, 4, name.length).set(name);
      header.setBigUint64(4 + name.length, BigInt(item.file.size), true);
      parts.push(header.buffer, item.file);
    }
    return new Blob(parts);
  }
  async function worker() {
    while (queue.length) {
      const batch = nextBatch();
      for (let attempt = 0; ; attempt++) {
        try {
          const response = await fetch(base + 'batch?target=' + target, { method: 'PUT', body: body(batch) });
          if (!response.ok) throw new Error(response.status);
          break;
        } catch (error) {
          failed++;
          if (attempt >= 5) { status.textContent = 'The phone stopped answering: choose the folder again to go on.'; throw error; }
          await new Promise(resolve => setTimeout(resolve, 2000 * (attempt + 1)));
        }
      }
      for (const item of batch) { done++; sent += item.file.size; }
      bar.value = sent; show();
    }
  }
  show();
  try {
    await Promise.all([worker(), worker(), worker(), worker()]);
    status.textContent = `Done: ${files.length} files on the phone.`;
  } catch { }
}
</script>
</body>
</html>
""";
}
