import 'dart:io';

import 'package:flutter/material.dart';
import 'package:path_provider/path_provider.dart';

import '../../common.dart';
import '../phone_workspace_build.dart';
import '../phone_workspace_release.dart';

void checkPhoneWorkspaceUpdate(BuildContext context) {
  showDialog<void>(context: context, builder: (_) => const _UpdateDialog());
}

class _UpdateDialog extends StatefulWidget {
  const _UpdateDialog();

  @override
  State<_UpdateDialog> createState() => _UpdateDialogState();
}

class _UpdateDialogState extends State<_UpdateDialog> {
  final _client = HttpClient()
    ..userAgent = 'RustDesk-phone-workspace/$phoneWorkspaceBuild'
    ..connectionTimeout = const Duration(seconds: 20);
  PhoneWorkspaceRelease? _release;
  File? _installer;
  String? _error;
  int? _percent;
  bool _busy = true;
  bool _needsPermission = false;

  @override
  void initState() {
    super.initState();
    _check();
  }

  @override
  void dispose() {
    _client.close(force: true);
    super.dispose();
  }

  Future<void> _check() async {
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      _release = await PhoneWorkspaceRelease.check(_client, android: isAndroid);
    } catch (e) {
      _error = e.toString();
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _install() async {
    if (_busy) return;
    setState(() {
      _busy = true;
      _error = null;
      _needsPermission = false;
      if (_installer == null) _percent = 0;
    });
    try {
      _installer ??= await _release!
          .download(_client, await getTemporaryDirectory(), (fraction) {
        final percent = (fraction * 100).floor();
        if (mounted && _percent != percent) setState(() => _percent = percent);
      });
      if (!mounted) return;
      if (isAndroid) {
        final started = await gFFI.invokeMethodWithResult<bool>(
            'install_phone_workspace_update', _installer!.path);
        if (started != true) {
          _needsPermission = true;
          return;
        }
      } else {
        final systemRoot = Platform.environment['SystemRoot']!;
        await Process.start(
            '$systemRoot/System32/msiexec.exe', ['/i', _installer!.path],
            mode: ProcessStartMode.detached);
      }
      if (mounted) Navigator.of(context).pop();
    } catch (e) {
      _error = e.toString();
    } finally {
      if (mounted) {
        setState(() {
          _busy = false;
          _percent = null;
        });
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final build = _release?.build;
    final available = build != null && build > phoneWorkspaceBuild;
    final percent = _percent;
    return AlertDialog(
      title: Text(translate('Check for updates')),
      content: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
                '${translate('Version')}   #$phoneWorkspaceBuild${available ? ' → #$build' : ''}'),
            if (build != null && !available)
              Text(translate('You are up to date')),
            if (_busy) ...[
              const SizedBox(height: 12),
              LinearProgressIndicator(
                  value: percent == null ? null : percent / 100),
              if (percent != null) Text('$percent%'),
            ],
            if (_needsPermission)
              Text(translate(
                  'Allow this app to install updates, then tap Install again.')),
            if (_error != null) Text('${translate('Error')}: $_error'),
          ]),
      actions: [
        TextButton(
            onPressed: () => Navigator.of(context).pop(),
            child: Text(translate(_busy ? 'Cancel' : 'Close'))),
        if (!_busy && (_error != null || available))
          TextButton(
            onPressed: build == null ? _check : _install,
            child: Text(translate(_error != null
                ? 'Retry'
                : _installer == null
                    ? 'Download new version'
                    : 'Install')),
          ),
      ],
    );
  }
}
