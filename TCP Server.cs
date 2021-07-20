using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace TCPServer
{
	class Server : IDisposable, IAsyncDisposable
	{
		/// <summary>
		/// メッセージ表示判定
		/// </summary>
		private readonly bool isMessage;

		/// <summary>
		/// ログ表示判定
		/// </summary>
		private readonly bool isLog;

		/// <summary>
		/// ファイルに書き込むログの内容
		/// </summary>
		private readonly BlockingCollection<string> msg = new();

		/// <summary>
		/// サーバのIPアドレス
		/// </summary>
		private IPAddress IP;

		/// <summary>
		/// サーバのポート番号
		/// </summary>
		private int Port;

		/// <summary>
		/// TCPポートがオープンしているか否かの判定
		/// </summary>
		private bool isOpen = false;

		/// <summary>
		/// TCPポートがオープンしているか否かの判定
		/// </summary>
		internal bool IsOpen
		{
			get { return isOpen; }
		}

		/// <summary>
		/// TCPサーバのソケット
		/// </summary>
		private Socket Sock;

		/// <summary>
		/// 受信バッファサイズ
		/// </summary>
		private int bufferSize;

		/// <summary>
		/// 接続待機のイベント
		/// </summary>
		private readonly ManualResetEventSlim AllDone = new(false);

		/// <summary>
		/// クライアント一覧
		/// </summary>
		private readonly SynchronizedCollection<SocketAsyncEventArgs> ClientSockets = new();

		/// <summary>
		/// 受信時に実行する外部メソッド
		/// 必要に応じて型を変える(型を増やす)
		/// </summary>
		private readonly Func<string, string> Method;

		/// <summary>
		/// コンストラクタ
		/// </summary>
		/// <param name="ismessage">true=エラー発生時にメッセージを表示する</param>
		/// <param name="islog">true=エラー発生時にログを記録する</param>
		/// <param name="method">データ受信時に実行するメソッド</param>
		internal Server(Func<string, string> method, bool ismessage = false, bool islog = false)
		{
			isMessage = ismessage;
			isLog = islog;
			Method = method;
			// Encoding.GetEncoding("shift_jis")で例外回避に必要
			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
			// エラーログを記録するならログ書き込みタスクを動作させる
			if (isLog == true)
			{
				logWrite(AppDomain.CurrentDomain.BaseDirectory + "TCP Server Error.csv");
			}
		}

		/// <summary>
		/// デストラクタで念のため閉じる処理
		/// </summary>
		~Server()
		{
			Close();
			msg.Dispose();
		}

		/// <summary>
		/// 終了時の処理
		/// </summary>
		public void Dispose()
		{
			Close();
			msg.Dispose();
			// これでデストラクタは呼ばれなくなるので無駄な終了処理がなくなる
			GC.SuppressFinalize(this);
		}

		/// <summary>
		/// 非同期な終了処理
		/// </summary>
		/// <returns></returns>
		public ValueTask DisposeAsync()
		{
			Close();
			msg.Dispose();
			// これでデストラクタは呼ばれなくなるので無駄な終了処理がなくなる
			GC.SuppressFinalize(this);
			return default;
		}

		/// <summary>
		/// ログの追加
		/// </summary>
		/// <param name="message">書き込むメッセージ</param>
		private void message(string message)
		{
			_ = msg.TryAdd(DateTime.Now.ToString("yyyy/M/d HH:mm:ss,") + message, Timeout.Infinite);
		}

		/// <summary>
		///  エラーログを記録
		/// </summary>
		/// <param name="message">エラー内容</param>
		private void logWrite(string fileName)
		{
			// 最初にファイルの有無を確認
			if (File.Exists(fileName) == false)
			{
				using FileStream hStream = File.Create(fileName);
				// 作成時に返される FileStream を利用して閉じる
				hStream?.Close();
			}
			_ = Task.Run(async () =>
			{
				while (true)
				{
					try
					{
						_ = msg.TryTake(out string log, Timeout.Infinite);
						while (true)
						{
							try
							{
								// ファイルがロックされている場合例外が発生して以下の処理は行わずリトライとなる
								using FileStream stream = new(fileName, FileMode.Open);
								// ログ書き込み
								using FileStream fs = new(fileName, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
								using StreamWriter sw = new(fs, Encoding.GetEncoding("Shift-JIS"));
								sw.WriteLine(log);
								break;
							}
							catch (Exception)
							{
								await Task.Delay(1000);
							}
						}
					}
					catch (Exception)
					{
					}
				}
			});
		}

		/// <summary>
		/// TCPポートのオープン
		/// </summary>
		/// <param name="ipAddress">TCPサーバのIPアドレス</param>
		/// <param name="port">TCPサーバが使用するポート番号</param>
		/// <param name="listen">最大接続数</param>
		/// <param name="buffersize">受信バッファのサイズ</param>
		/// <returns></returns>
		internal bool Open(string ipAddress, int port, int listen, int buffersize)
		{
			// まだポートがオープンしけなければ処理
			if (isOpen == false)
			{
				bufferSize = buffersize;
				AllDone.Set();
				// 指定されたIPアドレスが正しい値かチェック
				if (IPAddress.TryParse(ipAddress, out IPAddress result) == true)
				{
					IP = result;
				}
				else
				{
					if (isLog == true)
						message("IPアドレス文字列が不適切です");
					if (isMessage == true)
						_ = MessageBox.Show("IPアドレス文字列が不適切です", "TCP Server オープン", MessageBoxButton.OK, MessageBoxImage.Error);
					return false;
				}

				// 引数のIPアドレスがPCに存在しているか確認(127.0.0.1は除く)
				if (ipAddress != "127.0.0.1")
				{
					if (new List<IPAddress>(Dns.GetHostAddresses(Dns.GetHostName())).ConvertAll(x => x.ToString()).Any(l => l == ipAddress) == false)
					{
						if (isLog == true)
							message("指定されたIPアドレスは存在しません。");
						if (isMessage == true)
							_ = MessageBox.Show("指定されたIPアドレスは存在しません。", "TCP Server オープン", MessageBoxButton.OK, MessageBoxImage.Error);
						return false;
					}
				}

				Port = port;
				Sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
				Sock.Bind(new IPEndPoint(IP, Port));
				Sock.Listen(listen);

				isOpen = true;
				accept();
			}

			return true;
		}

		/// <summary>
		/// 接続待機を別タスクで実行
		/// </summary>
		private void accept()
		{
			_ = Task.Run(() =>
			{
				while (true)
				{
					AllDone.Reset();
					try
					{
						SocketAsyncEventArgs args = new();
						args.Completed += new EventHandler<SocketAsyncEventArgs>(ConnectCompleted);
						if (Sock.AcceptAsync(args) == true)
						{
							// 非同期処理が完了するまで待機
							AllDone.Wait();
						}
						else
						{
							// 同期的に完了した場合はこちらの処理
							// Serverでのイベントは不要なので削除する
							args.Completed -= new EventHandler<SocketAsyncEventArgs>(ConnectCompleted);
							ClientSockets.Add(args);
							_ = new Client(this, args, bufferSize);
						}
					}
					catch (ObjectDisposedException)
					{
						// オブジェクトが閉じられていれば終了
						break;
					}
					catch (Exception e)
					{
						if (isLog == true)
						{
							message(e.Message);
							continue;
						}
					}
				}
			});
		}

		/// <summary>
		/// クライアント接続完了イベント
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void ConnectCompleted(object sender, SocketAsyncEventArgs e)
		{
			// e.LastOperationでイベント種類が判別できる
			if (e.LastOperation == SocketAsyncOperation.Accept)
			{
				AllDone.Set();
				// Serverでのイベントは不要なので削除する
				e.Completed -= new EventHandler<SocketAsyncEventArgs>(ConnectCompleted);
				// 接続中のクライアントを追加
				ClientSockets.Add(e);
				_ = new Client(this, e, bufferSize);
			}
		}

		/// <summary>
		/// ソケットを閉じる
		/// </summary>
		internal void Close()
		{
			// 接続されているTCPクライアントがあれば切断する
			foreach (SocketAsyncEventArgs c in ClientSockets)
			{
				c.AcceptSocket?.Shutdown(SocketShutdown.Both);
				c.AcceptSocket?.Close();
			}
			ClientSockets.Clear();
			Sock?.Close();
			isOpen = false;
		}

		/// <summary>
		/// 相手が切断したのでリスト破棄要求
		/// </summary>
		/// <param name="e"></param>
		internal void Remove(SocketAsyncEventArgs e)
		{
			_ = ClientSockets.Remove(e);
		}


		/// <summary>
		/// 接続してきたクライアントとのやり取りするClass
		/// </summary>
		class Client
		{
			/// <summary>
			/// Clientクラスのインスタンスを生成したServerのClass
			/// </summary>
			private readonly Server server;

			/// <summary>
			/// 送受信データサイズ
			/// </summary>
			private readonly int bufferSize;

			/// <summary>
			/// 非同期ソケット通信インスタンス
			/// </summary>
			internal SocketAsyncEventArgs args;

			/// <summary>
			/// 受信イベント
			/// </summary>
			private readonly ManualResetEventSlim receiveDone = new(false);

			/// <summary>
			/// 送信イベント
			/// </summary>
			private readonly ManualResetEventSlim sendsDone = new(false);

			/// <summary>
			/// コンストラクタ
			/// </summary>
			/// <param name="e"></param>
			public Client(Server s, SocketAsyncEventArgs e, int BufferSize)
			{
				server = s;
				args = e;
				bufferSize = BufferSize;
				args.Completed += new EventHandler<SocketAsyncEventArgs>(completed);
				transceiver();
			}

			/// <summary>
			/// 送受信動作
			/// </summary>
			/// <param name="args"></param>
			private void transceiver()
			{
				_ = Task.Run(() =>
				{
					while (true)
					{
						try
						{
							// まずはクライアントから受信
							var receiveBuffer = new byte[bufferSize];
							args.SetBuffer(receiveBuffer, 0, bufferSize);
							receiveDone.Reset();
							if (args.AcceptSocket.ReceiveAsync(args) == true)
							{
								receiveDone.Wait();
							}
							// 実験では受信したデータをそのまま返す
							byte[] sendBuffer = new byte[args.BytesTransferred];
							Buffer.BlockCopy(receiveBuffer, 0, sendBuffer, 0, args.BytesTransferred);

							// 受信サイズが0以外なら受信処理
							if (args.BytesTransferred != 0)
							{
								args.SetBuffer(sendBuffer, 0, sendBuffer.Length);
								sendsDone.Reset();
								if (args.AcceptSocket.SendAsync(args) == true)
								{
									sendsDone.Wait();
								}
							}
							else
							{
								if (args.AcceptSocket.Connected == true)
								{
									args.AcceptSocket.Shutdown(SocketShutdown.Both);
									args.AcceptSocket.Close();
								}
								server.Remove(args);
								args.Dispose();
								break;
							}
						}
						catch (ObjectDisposedException)
						{
							// オブジェクトが閉じられていれば終了
							args.Dispose();
							break;
						}
					}
				});
			}

			/// <summary>
			/// 送受信のCompletedイベント
			/// </summary>
			/// <param name="sender"></param>
			/// <param name="e"></param>
			private void completed(object sender, SocketAsyncEventArgs e)
			{
				// e.LastOperationでイベント種類が判別できる
				switch (e.LastOperation)
				{
					case SocketAsyncOperation.Receive:
						receiveDone.Set();
						break;
					case SocketAsyncOperation.Send:
						sendsDone.Set();
						break;
				}
			}
		}
	}
}
