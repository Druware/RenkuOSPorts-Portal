project('renkuports-builder', 'c', version: '0.1.2', license: 'MIT', meson_version: '>= 0.60',
  default_options: ['c_std=gnu11', 'warning_level=2'])

add_project_arguments('-DAPP_VERSION="@0@"'.format(meson.project_version()), language: 'c')

core_deps = [
  dependency('gio-unix-2.0', version: '>= 2.74'),
  dependency('libsoup-3.0'),
  dependency('json-glib-1.0'),
]
core = static_library('builder-core', 'src/settings.c', 'src/core.c', 'src/api.c', 'src/worker.c',
  dependencies: core_deps)
core_dep = declare_dependency(link_with: core, dependencies: core_deps, include_directories: 'src')

executable('renkuports-builder', 'src/main.c', dependencies: [core_dep, dependency('gtk4')], install: true)
install_data('data/org.renkuos.PortsBuilder.desktop', install_dir: get_option('datadir') / 'applications')

test('core', executable('test-core', 'tests/test_core.c', dependencies: core_dep))
test('e2e', find_program('tests/e2e.sh'), args: [meson.current_build_dir() / 'renkuports-builder'],
  timeout: 120, is_parallel: false)
