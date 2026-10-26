"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const { readFile } = require("node:fs/promises");
const path = require("node:path");
const vm = require("node:vm");
const captureUtils = require("../src/shared.js");

function validCapture() {
  return {
    fields: {
      title: { value: "Blue Period", selector: "h1" },
      category: { value: "Manga", selector: ".category" },
      chapterNumber: { value: "12", selector: ".chapter-number" },
      description: { value: "An art story.", selector: ".summary" }
    },
    images: {
      selections: [
        {
          selector: "#pages",
          tagName: "ol",
          page: {
            url: "https://example.test/read/12",
            title: "Chapter 12"
          },
          capturedCount: 2
        }
      ],
      items: [
        { url: "https://example.test/page-a.png" },
        { url: "https://example.test/page-b.webp" }
      ]
    },
    settings: {
      isPrivate: false
    },
    sourcePages: [
      {
        url: "https://example.test/item",
        title: "Example"
      }
    ]
  };
}

test("download handler queues metadata, info, and images concurrently", async () => {
  const downloads = [];
  const downloadCallbacks = [];
  let messageHandler;
  const context = {
    URL,
    encodeURIComponent,
    globalThis: null,
    importScripts() {},
    UMDCaptureUtils: captureUtils,
    chrome: {
      runtime: {
        lastError: null,
        onMessage: {
          addListener(handler) {
            messageHandler = handler;
          }
        }
      },
      downloads: {
        download(options, callback) {
          downloads.push(options);
          downloadCallbacks.push(callback);
        }
      }
    }
  };
  context.globalThis = context;

  const source = await readFile(
    path.join(__dirname, "..", "src", "background.js"),
    "utf8"
  );
  vm.runInNewContext(source, context, {
    filename: "src/background.js"
  });

  assert.equal(typeof messageHandler, "function");

  const responsePromise = new Promise((resolve) => {
    const staysOpen = messageHandler(
      {
        type: "UMD_CAPTURE_DOWNLOAD",
        capture: validCapture()
      },
      {},
      resolve
    );

    assert.equal(staysOpen, true);
  });

  assert.equal(downloads.length, 4);
  assert.equal(downloadCallbacks.length, 4);
  downloadCallbacks.forEach((callback, index) => callback(index + 1));

  const response = await responsePromise;
  assert.equal(response.ok, true);
  assert.match(downloads[0].filename, /\/metadata\.json$/u);
  assert.match(downloads[0].url, /^data:application\/json/u);
  assert.match(downloads[1].filename, /\/info\.json$/u);
  assert.match(downloads[1].url, /^data:application\/json/u);
  assert.match(downloads[2].filename, /\/chapters\/12\/Bl_0\.png$/u);
  assert.match(downloads[3].filename, /\/chapters\/12\/Bl_1\.webp$/u);
  const infoDocument = JSON.parse(
    decodeURIComponent(downloads[1].url.slice(downloads[1].url.indexOf(",") + 1))
  );
  assert.equal(
    infoDocument.items[0].units[0].sources[0].filePath,
    "chapters/12"
  );
  assert.equal(
    infoDocument.items[0].units[0].sources[0].torrent.infoHashV1,
    undefined
  );
  assert.equal(response.result.metadataFiles, 1);
  assert.equal(response.result.infoFiles, 1);
  assert.equal(response.result.startedImages, 2);
  assert.equal(response.result.failures.length, 0);
});

test("download handler requires a captured title", async () => {
  let messageHandler;
  const context = {
    URL,
    encodeURIComponent,
    globalThis: null,
    importScripts() {},
    UMDCaptureUtils: captureUtils,
    chrome: {
      runtime: {
        lastError: null,
        onMessage: {
          addListener(handler) {
            messageHandler = handler;
          }
        }
      },
      downloads: {
        download() {
          assert.fail("A download must not start without a title.");
        }
      }
    }
  };
  context.globalThis = context;

  const source = await readFile(
    path.join(__dirname, "..", "src", "background.js"),
    "utf8"
  );
  vm.runInNewContext(source, context, {
    filename: "src/background.js"
  });

  const response = await new Promise((resolve) => {
    messageHandler(
      {
        type: "UMD_CAPTURE_DOWNLOAD",
        capture: {
          fields: {},
          images: { items: [] }
        }
      },
      {},
      resolve
    );
  });

  assert.equal(response.ok, false);
  assert.match(response.error, /Capture a title/u);
});

test("capture state persists and appends images across page changes", async () => {
  let messageHandler;
  let storedState;
  const context = {
    URL,
    Date,
    encodeURIComponent,
    globalThis: null,
    importScripts() {},
    UMDCaptureUtils: captureUtils,
    chrome: {
      runtime: {
        lastError: null,
        onMessage: {
          addListener(handler) {
            messageHandler = handler;
          }
        },
        sendMessage() {
          return Promise.resolve();
        }
      },
      storage: {
        local: {
          async get(key) {
            return storedState ? { [key]: storedState } : {};
          },
          async set(values) {
            storedState = values.umdCatalogCaptureDraft;
          }
        }
      },
      downloads: {
        download() {
          assert.fail("This test does not download files.");
        }
      }
    }
  };
  context.globalThis = context;

  const source = await readFile(
    path.join(__dirname, "..", "src", "background.js"),
    "utf8"
  );
  vm.runInNewContext(source, context, {
    filename: "src/background.js"
  });

  async function send(message, sender = {}) {
    return await new Promise((resolve) => {
      messageHandler(message, sender, resolve);
    });
  }

  const titleResponse = await send(
    {
      type: "UMD_CAPTURE_SAVE_FIELD",
      field: "title",
      selection: {
        value: "Blue Period",
        selector: "h1",
        tagName: "h1",
        page: {
          url: "https://example.test/details",
          title: "Details"
        }
      }
    },
    { tab: { id: 1 } }
  );
  assert.equal(titleResponse.ok, true);

  const firstImages = await send(
    {
      type: "UMD_CAPTURE_ADD_IMAGES",
      selection: {
        selector: "#page-one",
        tagName: "div",
        page: {
          url: "https://example.test/read/1",
          title: "Reader page 1"
        },
        items: [{ url: "https://cdn.example.test/a.jpg" }]
      }
    },
    { tab: { id: 1 } }
  );
  assert.equal(firstImages.ok, true);

  const secondImages = await send(
    {
      type: "UMD_CAPTURE_ADD_IMAGES",
      selection: {
        selector: "#page-two",
        tagName: "div",
        page: {
          url: "https://example.test/read/2",
          title: "Reader page 2"
        },
        items: [
          { url: "https://cdn.example.test/a.jpg" },
          { url: "https://cdn.example.test/b.jpg" }
        ]
      }
    },
    { tab: { id: 1 } }
  );
  assert.equal(secondImages.ok, true);
  assert.equal(secondImages.state.fields.title.value, "Blue Period");
  assert.equal(secondImages.state.images.items.length, 2);
  assert.equal(secondImages.state.images.selections.length, 2);
  assert.equal(secondImages.state.sourcePages.length, 3);

  const reload = await send({ type: "UMD_CAPTURE_GET_STATE" });
  assert.equal(reload.state.fields.title.value, "Blue Period");
  assert.equal(reload.state.images.items.length, 2);
});
