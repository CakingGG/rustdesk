import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:flutter_hbb/common/phone_workspace_build.dart';
import 'package:flutter_hbb/common/phone_workspace_release.dart';
import 'package:flutter_test/flutter_test.dart';

class _LocalGitHub extends Fake implements HttpClient {
  final HttpClient client;
  final int port;
  final bool serveMirror;
  _LocalGitHub(this.client, this.port, {this.serveMirror = false});

  @override
  Future<HttpClientRequest> getUrl(Uri uri) {
    if (uri.host == phoneWorkspaceDownloadHost && !serveMirror) {
      throw const SocketException('Mirror offline');
    }
    return client
        .getUrl(uri.replace(scheme: 'http', host: '127.0.0.1', port: port));
  }
}

void main() {
  final payload = utf8.encode('test installer');
  final digest = 'sha256:${sha256.convert(payload)}';
  Map<String, dynamic> release(int n, {List<int>? installer}) => {
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
              'size': installer?.length ?? payload.length,
              'digest': installer == null
                  ? digest
                  : 'sha256:${sha256.convert(installer)}'
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
      'uses the mirror directly and falls back to GitHub for a damaged mirror copy',
      () async {
    final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    final client = HttpClient();
    final cache = await Directory.systemTemp.createTemp();
    addTearDown(() async {
      client.close(force: true);
      await server.close(force: true);
      await cache.delete(recursive: true);
    });
    var damaged = false;
    final paths = <String>[];
    server.listen((request) async {
      if (request.uri.path.endsWith('/releases')) {
        request.response.write(jsonEncode([release(18)]));
      } else {
        paths.add(request.uri.path);
        if (request.uri.path.startsWith('/rustdesk/')) {
          expect(request.headers.value(HttpHeaders.rangeHeader), isNull);
          request.response.add(damaged ? payload.reversed.toList() : payload);
        } else {
          request.response.add(payload);
        }
      }
      await request.response.close();
    });
    final github = _LocalGitHub(client, server.port, serveMirror: true);
    final update = await PhoneWorkspaceRelease.check(github, android: true);
    final mirror = '/rustdesk/${update.digest}-${update.assetName}';
    expect(await (await update.download(github, cache, (_) {})).readAsBytes(),
        payload);
    expect(paths, [mirror]);
    damaged = true;
    paths.clear();
    expect(await (await update.download(github, cache, (_) {})).readAsBytes(),
        payload);
    expect(paths, [
      mirror,
      '/CakingGG/rustdesk/releases/download/phone-workspace-18/${update.assetName}'
    ]);
  }, skip: phoneWorkspaceDownloadHost.isEmpty);

  test(
      'downloads ranges concurrently and finishes an interrupted range from the next byte',
      () async {
    final bytes =
        List<int>.generate(2 * 1024 * 1024 + 13, (i) => (i ~/ 31 + i) % 251);
    final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    final client = HttpClient();
    final cache = await Directory.systemTemp.createTemp();
    addTearDown(() async {
      client.close(force: true);
      await server.close(force: true);
      await cache.delete(recursive: true);
    });
    final concurrent = Completer<void>();
    var overlapped = false;
    final starts = <int>[];
    const interruptedAt = 317;
    server.listen((request) async {
      final response = request.response;
      final range = request.headers.value(HttpHeaders.rangeHeader);
      if (request.uri.path.endsWith('/releases')) {
        response.write(jsonEncode([release(16, installer: bytes)]));
      } else if (range == null) {
        response.add(bytes);
      } else {
        final offsets =
            range.substring('bytes='.length).split('-').map(int.parse);
        final start = offsets.first, end = offsets.last;
        starts.add(start);
        response.statusCode = HttpStatus.partialContent;
        response.headers.set(HttpHeaders.contentRangeHeader,
            'bytes $start-$end/${bytes.length}');
        if (start == 0) {
          response.add(bytes.sublist(0, interruptedAt));
          await response.flush();
          await concurrent.future
              .timeout(const Duration(seconds: 5), onTimeout: () {});
          overlapped = concurrent.isCompleted;
        } else {
          if (!concurrent.isCompleted) concurrent.complete();
          response.add(bytes.sublist(start, end + 1));
        }
      }
      await response.close();
    });
    final github = _LocalGitHub(client, server.port);
    final update = await PhoneWorkspaceRelease.check(github, android: false);
    final file = await update.download(github, cache, (_) {});
    expect(
        (await sha256.bind(file.openRead()).first).toString(), update.digest);
    expect(overlapped, isTrue);
    expect(starts, contains(interruptedAt));
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
