(() => {
  let el = null;
  function render() {
    if (el) el.innerHTML = '<div class="prs-note">Loading pull requests</div>';
  }
  function mount(target) {
    el = target;
    render();
  }
  window.PrPane = { mount, render };
})();
