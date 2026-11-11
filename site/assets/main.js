/* Tabs and copy buttons. Everything degrades to plain markup without JS:
   the stylesheet only hides inactive panels under .js. */
(function () {
  "use strict";

  function initTabs(root) {
    var tabs = Array.prototype.slice.call(root.querySelectorAll("[data-tab]"));
    var panels = Array.prototype.slice.call(root.querySelectorAll("[data-panel]"));
    if (!tabs.length || tabs.length !== panels.length) return;

    function select(index, focus) {
      tabs.forEach(function (tab, i) {
        var active = i === index;
        tab.setAttribute("aria-selected", active ? "true" : "false");
        tab.tabIndex = active ? 0 : -1;
        panels[i].hidden = !active;
      });
      if (focus) tabs[index].focus();
    }

    tabs.forEach(function (tab, index) {
      tab.addEventListener("click", function () {
        select(index, false);
      });
      tab.addEventListener("keydown", function (event) {
        var last = tabs.length - 1;
        var next = null;
        switch (event.key) {
          case "ArrowRight":
          case "ArrowDown":
            next = index === last ? 0 : index + 1;
            break;
          case "ArrowLeft":
          case "ArrowUp":
            next = index === 0 ? last : index - 1;
            break;
          case "Home":
            next = 0;
            break;
          case "End":
            next = last;
            break;
          default:
            return;
        }
        event.preventDefault();
        select(next, true);
      });
    });

    var initial = tabs.findIndex(function (tab) {
      return tab.getAttribute("aria-selected") === "true";
    });
    select(initial < 0 ? 0 : initial, false);
  }

  function copyText(text) {
    if (navigator.clipboard && window.isSecureContext) {
      return navigator.clipboard.writeText(text);
    }
    return new Promise(function (resolve, reject) {
      var area = document.createElement("textarea");
      area.value = text;
      area.setAttribute("readonly", "");
      area.style.position = "fixed";
      area.style.opacity = "0";
      document.body.appendChild(area);
      area.select();
      try {
        if (document.execCommand("copy")) {
          resolve();
        } else {
          reject(new Error("copy failed"));
        }
      } catch (error) {
        reject(error);
      } finally {
        document.body.removeChild(area);
      }
    });
  }

  function initCopyButtons() {
    var blocks = document.querySelectorAll("pre.code");
    Array.prototype.forEach.call(blocks, function (pre) {
      var wrap = document.createElement("div");
      wrap.className = "copy-wrap";
      pre.parentNode.insertBefore(wrap, pre);
      wrap.appendChild(pre);

      var button = document.createElement("button");
      button.type = "button";
      button.className = "copy";
      button.textContent = "复制";
      button.setAttribute("aria-label", "复制这段代码");
      wrap.appendChild(button);

      button.addEventListener("click", function () {
        copyText(pre.innerText.replace(/\s+$/, "")).then(
          function () {
            button.textContent = "已复制";
            button.classList.add("is-done");
            window.setTimeout(function () {
              button.textContent = "复制";
              button.classList.remove("is-done");
            }, 1600);
          },
          function () {
            button.textContent = "复制失败";
            window.setTimeout(function () {
              button.textContent = "复制";
            }, 1600);
          }
        );
      });
    });
  }

  Array.prototype.forEach.call(document.querySelectorAll("[data-tabs]"), initTabs);
  initCopyButtons();
})();
