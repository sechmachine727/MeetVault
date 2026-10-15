// Minimal element builder. Keeps the views readable without a framework and without innerHTML
// (which the page CSP forbids anyway).

export function el(tag, props = {}, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(props || {})) {
    if (value === undefined || value === null || value === false) continue;
    if (key === "class") node.className = value;
    else if (key === "text") node.textContent = value;
    else if (key === "dataset") Object.assign(node.dataset, value);
    else if (key === "style") Object.assign(node.style, value); // CSSOM: allowed under style-src 'self'
    else if (key.startsWith("on") && typeof value === "function") node.addEventListener(key.slice(2), value);
    else if (key in node) node[key] = value;
    else node.setAttribute(key, value === true ? "" : String(value));
  }
  for (const child of children.flat()) {
    if (child === null || child === undefined || child === false) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}

export function clear(node) {
  while (node.firstChild) node.removeChild(node.firstChild);
  return node;
}

// `Node.append(null)` does NOT skip the argument — it stringifies it, so a conditional child
// (`cond ? el(...) : null`) renders the literal text "null" in the page. Always append through
// this helper when any child can be null/undefined/false.
export function appendAll(node, ...children) {
  for (const child of children.flat()) {
    if (child === null || child === undefined || child === false) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}

// XSS-safe text join for server-provided strings.
export function textList(items, separator = " · ") {
  return (items || []).map((item) => String(item)).join(separator);
}
