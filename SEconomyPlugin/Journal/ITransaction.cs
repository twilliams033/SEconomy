﻿/*
 * This file is part of SEconomy - A server-sided currency implementation
 * Copyright (C) 2013-2014, Tyler Watson <tyler@tw.id.au>
 *
 * This program is free software; you can redistribute it and/or
 * modify it under the terms of the GNU General Public License
 * as published by the Free Software Foundation; either version 2
 * of the License, or (at your option) any later version.
 * 
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 * 
 * You should have received a copy of the GNU General Public License
 * along with this program; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Wolfje.Plugins.SEconomy.Journal {
	public interface ITransaction {

		long BankAccountTransactionK { get; set; }

		long BankAccountFK { get; set; }

		Money Amount { get; set; }

		string Message { get; set; }

		BankAccountTransactionFlags Flags { get; set; }

		BankAccountTransactionFlags Flags2 { get; set; }

		DateTime TransactionDateUtc { get; set; }

		long BankAccountTransactionFK { get; set; }

		IBankAccount BankAccount { get; }

		ITransaction OppositeTransaction { get; }

		Dictionary<string, object> CustomValues { get; }
	}
}
