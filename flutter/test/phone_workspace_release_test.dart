import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:flutter_hbb/common/phone_workspace_release.dart';
import 'package:flutter_test/flutter_test.dart';

class _LocalGitHub extends Fake implements HttpClient {
  final HttpClient client;
  final int port;
  _LocalGitHub(this.client, this.port);

  @override
  Future<HttpClientRequest> getUrl(Uri uri) =>
      client.getUrl(uri.replace(scheme: 'http', host: '127.0.0.1', port: port));
}

void main() {
  final payload = utf8.encode('test installer');
  final digest = 'sha256:${sha256.convert(payload)}';
  Map<String, dynamic> release(int n) => {
        'tag_name': 'phone-workspace-$n',
        'draft': false,
        'assets': [
          for (final name in [
            'phone-workspace-arm64.apk',
            'phone-workspace-x64.msi'
          ])
            {
              'name': name,
              'state': 'uploaded',
              'size': payload.length,
              'digest': digest
            }
        ],
      };

  test(
      'checks the workspace channel and downloads the verified platform installer',
      () async {
    final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    final client = HttpClient();
    final cache = await Directory.systemTemp.createTemp();
    addTearDown(() async {
      client.close(force: true);
      await server.close(force: true);
      await cache.delete(recursive: true);
    });
    final paths = <String>[];
    server.listen((request) async {
      paths.add(request.uri.path);
      if (request.uri.path.endsWith('/releases')) {
        request.response.write(jsonEncode([
          {'tag_name': '1.9.0', 'draft': false},
          {...release(9), 'assets': []},
          release(14),
          {...release(15), 'draft': true}
        ]));
      } else {
        request.response.add(payload);
      }
      await request.response.close();
    });
    final github = _LocalGitHub(client, server.port);
    for (final android in [true, false]) {
      final update =
          await PhoneWorkspaceRelease.check(github, android: android);
      final name =
          android ? 'phone-workspace-arm64.apk' : 'phone-workspace-x64.msi';
      expect(update.build, 14);
      expect(await (await update.download(github, cache, (_) {})).readAsBytes(),
          payload);
      expect(paths.last,
          '/CakingGG/rustdesk/releases/download/phone-workspace-14/$name');
    }
  });

  test(
      'rejects an installer with different bytes and discards the partial file',
      () async {
    final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    final client = HttpClient();
    final cache = await Directory.systemTemp.createTemp();
    addTearDown(() async {
      client.close(force: true);
      await server.close(force: true);
      await cache.delete(recursive: true);
    });
    server.listen((request) async {
      if (request.uri.path.endsWith('/releases')) {
        request.response.write(jsonEncode([release(1)]));
      } else {
        request.response.add(payload.reversed.toList());
      }
      await request.response.close();
    });
    final github = _LocalGitHub(client, server.port);
    final update = await PhoneWorkspaceRelease.check(github, android: true);
    await expectLater(
        update.download(github, cache, (_) {}), throwsFormatException);
    expect(cache.listSync(recursive: true).whereType<File>(), isEmpty);
  });
}
